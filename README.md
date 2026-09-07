# InputTracker

Windows 10/11 x64 本地绿色版输入统计工具（开发中）。

当前集成版本提供按设备 Raw Input 后台采集、程序目录内 Storage、键盘周期统计、键盘热力数据和逐设备鼠标按键次数。程序不保存鼠标移动位置或路线。首次确认、暂停/恢复、托盘、锁屏/挂起暂停及显式开机启动授权均由用户控制。范围删除、备份恢复 UI 与真实双键盘双鼠标终验仍在进行中。

本地构建使用仓库内 .NET 10 SDK：

```powershell
& .\.tools\dotnet\dotnet.exe restore InputTracker.sln
& .\.tools\dotnet\dotnet.exe build InputTracker.sln -c Release --no-restore
& .\.tools\dotnet\dotnet.exe test InputTracker.sln -c Release --no-build
& .\.tools\dotnet\dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true --no-build
```

隐私边界与阶段验收见 [docs/acceptance-plan.md](docs/acceptance-plan.md)，绿色版使用与卸载见 [docs/portable/README.md](docs/portable/README.md)。
