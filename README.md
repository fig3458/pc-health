# PC Health（电脑健康速览）

即点即用的轻量 Windows 小工具：图形界面汇总磁盘 SMART 是否良好、通电时间、寿命粗估，以及 CPU / 主板 / 磁盘等温度是否偏高。

## 特点

- 启动时自动请求管理员权限（读取 SMART / 传感器需要）
- 目标 **.NET Framework 4.8**（Windows 10/11 一般自带）
- 界面结论：良好 / 有警告 / 不良
- CPU、主板温度通过 LibreHardwareMonitor 读传感器

## 下载与使用

1. 到 [Releases](https://github.com/fig3458/pc-health/releases) 下载 `PcHealth-windows-x64-v1.0.0.zip`
2. **解压整个文件夹**（需同目录 DLL）
3. 双击 `PcHealth.exe`，UAC 点「是」

包含文件示例：

- `PcHealth.exe`
- `LibreHardwareMonitorLib.dll`
- `HidSharp.dll`
- 其他依赖 DLL / config

## 自行编译

```powershell
dotnet build -c Release
Copy-Item .\bin\Release\net48\* .\dist\ -Force
```

## 说明

- 寿命为粗估，仅供参考
- 个别主板芯片不受支持时，CPU/主板可能显示「暂无读数」
- 杀软有时会拦截硬件监控组件，可加白名单

## 许可证

[MIT](LICENSE)
