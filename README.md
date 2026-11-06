# PC Health（电脑健康速览）

即点即用的轻量 Windows 小工具：图形界面汇总磁盘 SMART 是否良好、通电时间、寿命粗估，以及 CPU / 主板 / 磁盘等温度是否偏高。

## 特点

- 启动时自动请求管理员权限（读取 SMART / CPU / 主板温度需要）
- 目标 **.NET Framework 4.8**（Windows 10/11 一般自带）
- 界面只显示结论：良好 / 有警告 / 不良好
- CPU、主板温度通过硬件传感器读取（LibreHardwareMonitor）

## 使用

把整个 `dist` 文件夹拷走即可（需同目录 DLL）：

- `PcHealth.exe`
- `LibreHardwareMonitorLib.dll`
- `HidSharp.dll`
- `System.CodeDom.dll`
- `PcHealth.exe.config`

双击运行，在 UAC 提示中点“是”。

## 编译

```powershell
dotnet build -c Release
Copy-Item .\bin\Release\net48\* .\dist\ -Force
```

## 说明

寿命为粗估。个别主板芯片不受支持时，CPU/主板可能显示“暂无读数”。杀软有时会拦截硬件监控组件，可加入白名单。
