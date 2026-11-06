using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Text;

namespace PcHealth
{
    public enum HealthLevel
    {
        Good,
        Warning,
        Bad,
        Unknown
    }

    public sealed class StatusItem
    {
        public string Title { get; set; } = "";
        public HealthLevel Level { get; set; } = HealthLevel.Unknown;
        public string Summary { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class HealthReport
    {
        public List<StatusItem> Disks { get; } = new List<StatusItem>();
        public List<StatusItem> Temps { get; } = new List<StatusItem>();
        public HealthLevel OverallDisk { get; set; } = HealthLevel.Unknown;
        public HealthLevel OverallTemp { get; set; } = HealthLevel.Unknown;
        public string Note { get; set; } = "";
        public TimeSpan Elapsed { get; set; }
    }

    public static class HealthCollector
    {
        // Rough thresholds used by many OEM utilities.
        private const int DiskWarnC = 50;
        private const int DiskHotC = 60;
        private const int CpuWarnC = 80;
        private const int CpuHotC = 95;
        private const int GpuWarnC = 80;
        private const int GpuHotC = 90;
        private const int ZoneWarnC = 75;
        private const int ZoneHotC = 90;
        private const int MoboWarnC = 65;
        private const int MoboHotC = 80;

        public static HealthReport Collect()
        {
            var sw = Stopwatch.StartNew();
            var report = new HealthReport();
            var notes = new List<string>();

            try
            {
                CollectDisks(report, notes);
            }
            catch (Exception ex)
            {
                report.Disks.Add(new StatusItem
                {
                    Title = "磁盘",
                    Level = HealthLevel.Unknown,
                    Summary = "无法读取",
                    Detail = ex.Message
                });
            }

            try
            {
                CollectTemps(report, notes);
            }
            catch (Exception ex)
            {
                report.Temps.Add(new StatusItem
                {
                    Title = "温度",
                    Level = HealthLevel.Unknown,
                    Summary = "无法读取",
                    Detail = ex.Message
                });
            }

            report.OverallDisk = Worst(report.Disks.Select(d => d.Level));
            report.OverallTemp = Worst(report.Temps.Select(t => t.Level));
            report.Note = string.Join(" ", notes.Distinct());
            sw.Stop();
            report.Elapsed = sw.Elapsed;
            return report;
        }

        private static void CollectDisks(HealthReport report, List<string> notes)
        {
            var disks = QueryPhysicalDisks();
            if (disks.Count == 0)
            {
                // Fallback: Win32_DiskDrive + predict status only
                disks = QueryWin32Disks();
            }

            var smartMap = QuerySmartPredict();
            var smartAttrs = QuerySmartAttributes();
            var reliability = QueryReliabilityCounters();
            var reliabilityList = reliability.Values.ToList();

            if (disks.Count == 0)
            {
                report.Disks.Add(new StatusItem
                {
                    Title = "磁盘",
                    Level = HealthLevel.Unknown,
                    Summary = "未检测到磁盘信息",
                    Detail = "可能缺少权限或驱动接口不可用"
                });
                notes.Add("磁盘详情读取受限。");
                return;
            }

            bool anyDetail = false;
            int index = 0;
            foreach (var d in disks)
            {
                index++;
                var title = string.IsNullOrWhiteSpace(d.FriendlyName)
                    ? $"磁盘 {index}"
                    : Truncate(d.FriendlyName, 28);

                var level = MapStorageHealth(d.HealthStatus);
                var predict = FindPredict(smartMap, d.DeviceId, d.PnpDeviceId, index - 1);
                if (predict.HasValue && predict.Value)
                    level = Escalate(level, HealthLevel.Bad);

                string? powerOn = null;
                string? life = null;
                int? diskTemp = null;
                SmartAttrSet? attrs = null;

                // Prefer Windows Storage reliability counters (works well for NVMe when elevated)
                ReliabilityInfo? rel = null;
                if (!string.IsNullOrEmpty(d.DeviceId) && reliability.TryGetValue(d.DeviceId, out var byId))
                    rel = byId;
                else if (index - 1 < reliabilityList.Count)
                    rel = reliabilityList[index - 1];

                if (rel != null)
                {
                    if (rel.PowerOnHours.HasValue)
                    {
                        powerOn = FormatPowerOn(rel.PowerOnHours.Value);
                        anyDetail = true;
                    }
                    if (rel.Temperature.HasValue)
                    {
                        diskTemp = rel.Temperature;
                        anyDetail = true;
                    }
                    if (rel.Wear.HasValue)
                    {
                        // Wear is "used" percentage on Windows
                        life = $"磨损已用约 {rel.Wear.Value}% · 寿命估计剩余 {Math.Max(0, 100 - rel.Wear.Value)}%";
                        anyDetail = true;
                        if (rel.Wear.Value >= 90) level = Escalate(level, HealthLevel.Warning);
                        if (rel.Wear.Value >= 95) level = Escalate(level, HealthLevel.Bad);
                    }
                    if ((rel.ReadErrorsUncorrected ?? 0) > 0 || (rel.WriteErrorsUncorrected ?? 0) > 0)
                        level = Escalate(level, HealthLevel.Warning);
                }

                if (smartAttrs.Count > 0)
                {
                    attrs = MatchAttrs(smartAttrs, index - 1);
                    if (attrs != null)
                    {
                        anyDetail = true;
                        if (powerOn == null && attrs.PowerOnHours.HasValue)
                            powerOn = FormatPowerOn(attrs.PowerOnHours.Value);
                        if (life == null)
                            life = EstimateLife(attrs, d.MediaType);
                        if (!diskTemp.HasValue)
                            diskTemp = attrs.TemperatureC;
                    }
                }

                if (level == HealthLevel.Good && attrs != null)
                {
                    if (attrs.Reallocated.HasValue && attrs.Reallocated.Value > 0)
                        level = HealthLevel.Warning;
                    if (attrs.Pending.HasValue && attrs.Pending.Value > 0)
                        level = HealthLevel.Warning;
                    if (attrs.WearUsedPercent.HasValue && attrs.WearUsedPercent.Value >= 90)
                        level = HealthLevel.Warning;
                    if (attrs.WearUsedPercent.HasValue && attrs.WearUsedPercent.Value >= 95)
                        level = HealthLevel.Bad;
                }

                // Still no life from counters/SMART: rough estimate from power-on if any
                if (life == null && rel?.PowerOnHours != null)
                {
                    life = EstimateLife(new SmartAttrSet { PowerOnHours = rel.PowerOnHours }, d.MediaType);
                }

                var summary = LevelText(level);
                var detailParts = new List<string>();
                detailParts.Add(MediaText(d.MediaType, d.BusType));
                if (!string.IsNullOrEmpty(powerOn))
                    detailParts.Add("通电 " + powerOn);
                if (!string.IsNullOrWhiteSpace(life))
                    detailParts.Add(life!);
                if (diskTemp.HasValue)
                    detailParts.Add($"盘温 {diskTemp.Value}°C");
                if (predict.HasValue && predict.Value)
                    detailParts.Add("SMART 预测故障");
                if (detailParts.Count == 1)
                    detailParts.Add(level == HealthLevel.Good ? "系统报告健康" : "详见系统磁盘健康状态");

                report.Disks.Add(new StatusItem
                {
                    Title = title,
                    Level = level,
                    Summary = summary,
                    Detail = string.Join(" · ", detailParts)
                });

                if (diskTemp.HasValue)
                {
                    report.Temps.Add(MakeTempItem($"磁盘 · {title}", diskTemp.Value, DiskWarnC, DiskHotC));
                }
            }

            if (!anyDetail)
                notes.Add("通电/寿命/盘温读取不完整（请确认已允许管理员权限）。");
        }

        private static void CollectTemps(HealthReport report, List<string> notes)
        {
            bool hasCpu = false;
            bool hasMobo = false;

            // CPU + motherboard via LibreHardwareMonitor (admin)
            foreach (var r in HardwareTemps.QueryCpuAndMotherboard())
            {
                if (r.Kind == "cpu")
                {
                    report.Temps.Add(MakeTempItem(r.Name, r.Celsius, CpuWarnC, CpuHotC));
                    hasCpu = true;
                }
                else
                {
                    report.Temps.Add(MakeTempItem(r.Name, r.Celsius, MoboWarnC, MoboHotC));
                    hasMobo = true;
                }
            }

            // ACPI zones as fallback / supplement (skip if we already have CPU+mobo)
            if (!hasCpu || !hasMobo)
            {
                foreach (var z in QueryAcpiZones())
                {
                    bool looksCpu = z.Name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (looksCpu && hasCpu) continue;
                    if (!looksCpu && hasMobo) continue;
                    int warn = looksCpu ? CpuWarnC : MoboWarnC;
                    int hot = looksCpu ? CpuHotC : MoboHotC;
                    report.Temps.Add(MakeTempItem(z.Name, z.Celsius, warn, hot));
                    if (looksCpu) hasCpu = true;
                    else hasMobo = true;
                }
            }

            // NVIDIA if present
            foreach (var g in QueryNvidiaTemps())
            {
                report.Temps.Add(MakeTempItem(g.Name, g.Celsius, GpuWarnC, GpuHotC));
            }

            if (!hasCpu)
            {
                report.Temps.Insert(0, new StatusItem
                {
                    Title = "CPU",
                    Level = HealthLevel.Unknown,
                    Summary = "暂无读数",
                    Detail = "未能读取 CPU 温度传感器"
                });
            }
            if (!hasMobo)
            {
                int insertAt = Math.Min(report.Temps.Count, hasCpu ? 1 : 0);
                report.Temps.Insert(insertAt, new StatusItem
                {
                    Title = "主板",
                    Level = HealthLevel.Unknown,
                    Summary = "暂无读数",
                    Detail = "未能读取主板温度传感器"
                });
            }

            if (report.Temps.All(t => t.Level == HealthLevel.Unknown))
                notes.Add("温度传感器不可用：部分主板芯片不受支持，或杀软拦截了硬件监控驱动。");
        }

        private static StatusItem MakeTempItem(string title, int celsius, int warn, int hot)
        {
            HealthLevel level;
            string summary;
            if (celsius >= hot)
            {
                level = HealthLevel.Bad;
                summary = $"高温 {celsius}°C";
            }
            else if (celsius >= warn)
            {
                level = HealthLevel.Warning;
                summary = $"偏高 {celsius}°C";
            }
            else
            {
                level = HealthLevel.Good;
                summary = $"正常 {celsius}°C";
            }

            return new StatusItem
            {
                Title = title,
                Level = level,
                Summary = summary,
                Detail = $"阈值：警告 ≥{warn}°C，危险 ≥{hot}°C"
            };
        }

        #region Disk queries

        private sealed class DiskInfo
        {
            public string FriendlyName = "";
            public string DeviceId = "";
            public string PnpDeviceId = "";
            public ushort HealthStatus;
            public ushort MediaType;
            public ushort BusType;
        }

        private static List<DiskInfo> QueryPhysicalDisks()
        {
            var list = new List<DiskInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT FriendlyName, DeviceId, HealthStatus, MediaType, BusType, PhysicalLocation FROM MSFT_PhysicalDisk");
                foreach (ManagementObject o in searcher.Get())
                {
                    list.Add(new DiskInfo
                    {
                        FriendlyName = o["FriendlyName"]?.ToString() ?? "",
                        DeviceId = o["DeviceId"]?.ToString() ?? "",
                        PnpDeviceId = o["PhysicalLocation"]?.ToString() ?? "",
                        HealthStatus = ToUshort(o["HealthStatus"]),
                        MediaType = ToUshort(o["MediaType"]),
                        BusType = ToUshort(o["BusType"])
                    });
                }
            }
            catch
            {
                // ignore
            }
            return list;
        }

        private static List<DiskInfo> QueryWin32Disks()
        {
            var list = new List<DiskInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Model, DeviceID, PNPDeviceID, InterfaceType, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject o in searcher.Get())
                {
                    list.Add(new DiskInfo
                    {
                        FriendlyName = o["Model"]?.ToString()?.Trim() ?? "Disk",
                        DeviceId = o["DeviceID"]?.ToString() ?? "",
                        PnpDeviceId = o["PNPDeviceID"]?.ToString() ?? "",
                        HealthStatus = 0, // unknown/healthy default; predict will override
                        MediaType = 0,
                        BusType = 0
                    });
                }
            }
            catch
            {
            }
            return list;
        }

        private static Dictionary<string, bool> QuerySmartPredict()
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
                foreach (ManagementObject o in searcher.Get())
                {
                    var name = o["InstanceName"]?.ToString() ?? "";
                    var fail = o["PredictFailure"] != null && Convert.ToBoolean(o["PredictFailure"]);
                    if (!string.IsNullOrEmpty(name))
                        map[name] = fail;
                }
            }
            catch
            {
            }
            return map;
        }

        private sealed class SmartAttrSet
        {
            public string InstanceName = "";
            public int? PowerOnHours;
            public int? TemperatureC;
            public int? Reallocated;
            public int? Pending;
            public int? WearUsedPercent; // 0-100 used
            public int? LifeRemainingPercent; // 0-100 remaining
        }

        private static List<SmartAttrSet> QuerySmartAttributes()
        {
            var list = new List<SmartAttrSet>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData");
                foreach (ManagementObject o in searcher.Get())
                {
                    var raw = o["VendorSpecific"] as byte[];
                    if (raw == null || raw.Length < 30) continue;
                    var set = ParseAtaSmart(raw);
                    set.InstanceName = o["InstanceName"]?.ToString() ?? "";
                    list.Add(set);
                }
            }
            catch
            {
            }
            return list;
        }

        private sealed class ReliabilityInfo
        {
            public int? PowerOnHours;
            public int? Temperature;
            public int? Wear;
            public int? ReadErrorsUncorrected;
            public int? WriteErrorsUncorrected;
        }

        private static Dictionary<string, ReliabilityInfo> QueryReliabilityCounters()
        {
            var map = new Dictionary<string, ReliabilityInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, PowerOnHours, Temperature, Wear, ReadErrorsUncorrected, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter");
                foreach (ManagementObject o in searcher.Get())
                {
                    var id = o["DeviceId"]?.ToString() ?? map.Count.ToString();
                    map[id] = new ReliabilityInfo
                    {
                        PowerOnHours = ToNullableInt(o["PowerOnHours"]),
                        Temperature = ToNullableInt(o["Temperature"]),
                        Wear = ToNullableInt(o["Wear"]),
                        ReadErrorsUncorrected = ToNullableInt(o["ReadErrorsUncorrected"]),
                        WriteErrorsUncorrected = ToNullableInt(o["WriteErrorsUncorrected"])
                    };
                }
            }
            catch
            {
            }
            return map;
        }

        private static int? ToNullableInt(object? v)
        {
            if (v == null || v is DBNull) return null;
            try
            {
                int n = Convert.ToInt32(v);
                // Windows sometimes returns sentinel large values for "unsupported"
                if (n < 0 || n > 1_000_000_000) return null;
                return n;
            }
            catch
            {
                return null;
            }
        }

        private static SmartAttrSet ParseAtaSmart(byte[] vendorSpecific)
        {
            // ATA SMART attribute table: 12-byte entries starting at offset 2
            var set = new SmartAttrSet();
            for (int i = 0; i < 30; i++)
            {
                int off = 2 + i * 12;
                if (off + 11 >= vendorSpecific.Length) break;
                int id = vendorSpecific[off];
                if (id == 0) continue;
                int value = vendorSpecific[off + 3];
                long raw = BitConverter.ToUInt32(vendorSpecific, off + 5);
                // some attrs use 6-byte raw; low 4 bytes usually enough for hours/temp/counts
                raw = vendorSpecific[off + 5]
                    | ((long)vendorSpecific[off + 6] << 8)
                    | ((long)vendorSpecific[off + 7] << 16)
                    | ((long)vendorSpecific[off + 8] << 24);

                switch (id)
                {
                    case 5: // Reallocated Sectors Count
                        set.Reallocated = (int)raw;
                        break;
                    case 9: // Power-On Hours
                        set.PowerOnHours = (int)Math.Min(int.MaxValue, raw);
                        break;
                    case 197: // Current Pending Sector
                        set.Pending = (int)raw;
                        break;
                    case 190: // Airflow Temperature
                    case 194: // Temperature
                        set.TemperatureC = (int)(raw & 0xFF);
                        if (set.TemperatureC < 1 || set.TemperatureC > 120)
                            set.TemperatureC = value; // sometimes normalized differently
                        break;
                    case 202: // Percent Lifetime Remain (some SSD)
                    case 232: // Available Reserved Space / endurance
                        if (value > 0 && value <= 100)
                            set.LifeRemainingPercent = value;
                        break;
                    case 177: // Wear Leveling Count (Samsung etc often value=remaining)
                    case 173: // Wear Leveling Count
                    case 231: // SSD Life Left
                        if (value > 0 && value <= 100)
                            set.LifeRemainingPercent = value;
                        break;
                    case 245: // Used Reserved Block / Percent Used variants
                        if (raw >= 0 && raw <= 100)
                            set.WearUsedPercent = (int)raw;
                        break;
                }
            }

            if (!set.WearUsedPercent.HasValue && set.LifeRemainingPercent.HasValue)
                set.WearUsedPercent = 100 - set.LifeRemainingPercent.Value;

            return set;
        }

        private static bool? FindPredict(Dictionary<string, bool> map, string deviceId, string pnp, int index)
        {
            if (map.Count == 0) return null;
            foreach (var kv in map)
            {
                if (!string.IsNullOrEmpty(pnp) && kv.Key.IndexOf(pnp, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
                if (!string.IsNullOrEmpty(deviceId) && kv.Key.IndexOf(deviceId, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }
            // fallback by order
            if (index >= 0 && index < map.Count)
                return map.Values.ElementAt(index);
            return map.Values.Any(v => v) ? true : (bool?)false;
        }

        private static SmartAttrSet? MatchAttrs(List<SmartAttrSet> attrs, int index)
        {
            if (index >= 0 && index < attrs.Count) return attrs[index];
            return attrs.FirstOrDefault();
        }

        private static HealthLevel MapStorageHealth(ushort status)
        {
            // 0 Healthy, 1 Warning, 2 Unhealthy, 3 Unknown, 4 ...
            switch (status)
            {
                case 0: return HealthLevel.Good;
                case 1: return HealthLevel.Warning;
                case 2: return HealthLevel.Bad;
                default: return HealthLevel.Unknown;
            }
        }

        private static string MediaText(ushort media, ushort bus)
        {
            string m = media switch
            {
                3 => "HDD",
                4 => "SSD",
                5 => "SCM",
                _ => "磁盘"
            };
            string b = bus switch
            {
                11 => "SATA",
                17 => "NVMe",
                7 => "USB",
                8 => "RAID",
                _ => ""
            };
            return string.IsNullOrEmpty(b) ? m : $"{m}/{b}";
        }

        private static string FormatPowerOn(int hours)
        {
            if (hours < 24) return $"{hours} 小时";
            int days = hours / 24;
            if (days < 365) return $"{days} 天（约 {hours} 小时）";
            double years = hours / 8760.0;
            return $"{days} 天（约 {years:0.0} 年）";
        }

        private static string EstimateLife(SmartAttrSet attrs, ushort mediaType)
        {
            if (attrs.LifeRemainingPercent.HasValue)
                return $"寿命估计剩余 {attrs.LifeRemainingPercent.Value}%";
            if (attrs.WearUsedPercent.HasValue)
                return $"磨损已用约 {attrs.WearUsedPercent.Value}%";

            // Very rough fallback from power-on hours only
            if (attrs.PowerOnHours.HasValue)
            {
                int designHours = mediaType == 4 ? 8760 * 5 : 8760 * 5; // ~5 years design rough
                int remain = Math.Max(0, 100 - (int)Math.Round(attrs.PowerOnHours.Value * 100.0 / designHours));
                remain = Math.Min(99, remain);
                return $"按通电粗估剩余约 {remain}%（仅供参考）";
            }
            return "寿命数据暂无";
        }

        #endregion

        #region Temp queries

        private sealed class TempReading
        {
            public string Name = "";
            public int Celsius;
        }

        private static List<TempReading> QueryAcpiZones()
        {
            var list = new List<TempReading>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                int i = 0;
                foreach (ManagementObject o in searcher.Get())
                {
                    i++;
                    // tenths of Kelvin
                    double tenths = Convert.ToDouble(o["CurrentTemperature"]);
                    int c = (int)Math.Round(tenths / 10.0 - 273.15);
                    if (c < 0 || c > 150) continue;
                    var inst = o["InstanceName"]?.ToString() ?? "";
                    string name = "系统热区";
                    if (inst.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0
                        || inst.IndexOf("PROC", StringComparison.OrdinalIgnoreCase) >= 0)
                        name = "CPU 热区";
                    else if (inst.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                        name = "GPU 热区";
                    else
                        name = list.Count == 0 ? "主板/系统温度" : $"系统热区 {i}";

                    list.Add(new TempReading { Name = name, Celsius = c });
                }
            }
            catch
            {
            }
            return list;
        }

        private static List<TempReading> QueryNvidiaTemps()
        {
            var list = new List<TempReading>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name,temperature.gpu --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return list;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1500);
                if (p.ExitCode != 0) return list;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;
                    var name = parts[0].Trim();
                    if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
                        continue;
                    list.Add(new TempReading
                    {
                        Name = "GPU · " + Truncate(name, 24),
                        Celsius = c
                    });
                }
            }
            catch
            {
                // no NVIDIA or tool missing
            }
            return list;
        }

        #endregion

        #region helpers

        public static string LevelText(HealthLevel level) => level switch
        {
            HealthLevel.Good => "良好",
            HealthLevel.Warning => "有警告",
            HealthLevel.Bad => "不良好",
            _ => "未知"
        };

        private static HealthLevel Worst(IEnumerable<HealthLevel> levels)
        {
            var arr = levels.ToList();
            if (arr.Count == 0) return HealthLevel.Unknown;
            if (arr.Any(l => l == HealthLevel.Bad)) return HealthLevel.Bad;
            if (arr.Any(l => l == HealthLevel.Warning)) return HealthLevel.Warning;
            if (arr.Any(l => l == HealthLevel.Good)) return HealthLevel.Good;
            return HealthLevel.Unknown;
        }

        private static HealthLevel Escalate(HealthLevel current, HealthLevel next)
        {
            int Rank(HealthLevel l) => l switch
            {
                HealthLevel.Bad => 3,
                HealthLevel.Warning => 2,
                HealthLevel.Good => 1,
                _ => 0
            };
            return Rank(next) > Rank(current) ? next : current;
        }

        private static ushort ToUshort(object? v)
        {
            if (v == null) return 0;
            try { return Convert.ToUInt16(v); } catch { return 0; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        #endregion
    }
}
