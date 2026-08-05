# InputTracker

Windows 10/11 x64 本地绿色版输入统计工具（开发中）。

当前 `bootstrap` 阶段提供可自包含发布的 WPF 安全外壳、程序目录可写检查、单实例和托盘暂停控制。它尚未采集任何键盘或鼠标输入。

本地构建使用仓库内 .NET 10 SDK：

```powershell
& .\.tools\dotnet\dotnet.exe restore InputTracker.sln
& .\.tools\dotnet\dotnet.exe build InputTracker.sln -c Release --no-restore
& .\.tools\dotnet\dotnet.exe test InputTracker.sln -c Release --no-build
& .\.tools\dotnet\dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true --no-build
```

隐私边界与阶段验收见 [docs/acceptance-plan.md](docs/acceptance-plan.md)，bootstrap 设计见 [docs/bootstrap.md](docs/bootstrap.md)。
