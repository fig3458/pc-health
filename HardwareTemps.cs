using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace PcHealth
{
    /// <summary>
    /// Reads CPU / motherboard temperatures via LibreHardwareMonitor (needs admin).
    /// </summary>
    internal static class HardwareTemps
    {
        private static readonly object Gate = new object();
        private static Computer? _computer;
        private static bool _failed;

        public sealed class Reading
        {
            public string Name = "";
            public int Celsius;
            public string Kind = ""; // cpu | motherboard
        }

        public static List<Reading> QueryCpuAndMotherboard()
        {
            var result = new List<Reading>();
            if (_failed) return result;

            try
            {
                lock (Gate)
                {
                    EnsureOpen();
                    if (_computer == null) return result;

                    int? cpu = null;
                    string cpuName = "CPU";
                    int? mobo = null;
                    string moboName = "主板";

                    foreach (var hw in _computer.Hardware)
                    {
                        UpdateRecursive(hw);

                        if (hw.HardwareType == HardwareType.Cpu)
                        {
                            var pick = PickCpuTemp(hw);
                            if (pick.HasValue)
                            {
                                cpu = pick.Value.temp;
                                cpuName = "CPU · " + Truncate(hw.Name, 22);
                            }
                        }
                        else if (hw.HardwareType == HardwareType.Motherboard)
                        {
                            var pick = PickMoboTemp(hw);
                            if (pick.HasValue)
                            {
                                mobo = pick.Value.temp;
                                moboName = "主板 · " + Truncate(pick.Value.label, 18);
                            }

                            // SuperIO chips often sit under motherboard as sub-hardware
                            foreach (var sub in hw.SubHardware)
                            {
                                if (mobo.HasValue) break;
                                var subPick = PickMoboTemp(sub);
                                if (subPick.HasValue)
                                {
                                    mobo = subPick.Value.temp;
                                    moboName = "主板 · " + Truncate(subPick.Value.label, 18);
                                }
                            }
                        }
                    }

                    if (cpu.HasValue)
                        result.Add(new Reading { Name = cpuName, Celsius = cpu.Value, Kind = "cpu" });
                    if (mobo.HasValue)
                        result.Add(new Reading { Name = moboName, Celsius = mobo.Value, Kind = "motherboard" });
                }
            }
            catch
            {
                _failed = true;
            }

            return result;
        }

        private static void EnsureOpen()
        {
            if (_computer != null) return;
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsGpuEnabled = false,
                    IsMemoryEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false,
                    IsControllerEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false
                };
                c.Open();
                _computer = c;
            }
            catch
            {
                _failed = true;
                _computer = null;
            }
        }

        private static void UpdateRecursive(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                UpdateRecursive(sub);
        }

        private static List<(string name, float value)> AllTemps(IHardware hardware)
        {
            var list = new List<(string, float)>();
            void Walk(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    if (!s.Value.HasValue) continue;
                    float v = s.Value.Value;
                    if (v < 1 || v > 125) continue;
                    list.Add((s.Name ?? "", v));
                }
                foreach (var sub in h.SubHardware)
                    Walk(sub);
            }
            Walk(hardware);
            return list;
        }

        private static (int temp, string label)? PickCpuTemp(IHardware cpu)
        {
            var temps = AllTemps(cpu);
            if (temps.Count == 0) return null;

            // Prefer package / Tctl over single core
            string[] prefer =
            {
                "Package", "CPU Package", "Tctl", "Tctl/Tdie", "CCD", "CPU", "Core (Tctl/Tdie)"
            };
            foreach (var key in prefer)
            {
                var hit = temps.FirstOrDefault(t =>
                    t.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(hit.name))
                    return ((int)Math.Round(hit.value), hit.name);
            }

            // Else use max core temperature (conservative)
            var max = temps.OrderByDescending(t => t.value).First();
            return ((int)Math.Round(max.value), max.name);
        }

        private static (int temp, string label)? PickMoboTemp(IHardware hw)
        {
            var temps = AllTemps(hw);
            if (temps.Count == 0) return null;

            // Prefer system / motherboard sensors; skip obvious CPU duplicates
            string[] prefer = { "System", "Motherboard", "Mother Board", "Ambient", "Chipset", "PCH", "AUXTIN", "Temperature #1", "Temp1" };
            foreach (var key in prefer)
            {
                var hit = temps.FirstOrDefault(t =>
                {
                    if (t.name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    if (t.name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                    return t.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                });
                if (!string.IsNullOrEmpty(hit.name))
                    return ((int)Math.Round(hit.value), hit.name);
            }

            var filtered = temps.Where(t =>
                t.name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) < 0 &&
                t.name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (filtered.Count == 0) return null;
            var pick = filtered.OrderByDescending(t => t.value).First();
            return ((int)Math.Round(pick.value), pick.name);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }
    }
}
