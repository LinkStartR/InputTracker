# InputTracker

Windows 10/11 x64 本地绿色版输入统计工具（开发中）。程序按设备采集键盘统计和鼠标按键次数，不保存输入文字、窗口信息、鼠标移动位置或路线。采集需首次明确同意，可随时暂停；数据、配置和备份保存在程序目录内。

当前代码提供日期范围查看、完整统计日范围删除、备份与离线恢复。删除前会创建备份；日内部分范围不支持。Storage 阶段自动验收覆盖了上述删除边界，但真实硬件、系统生命周期和恢复操作仍未完成最终现场验收，因此当前版本不应视为正式发布版。

## 从全新克隆构建

在 Windows PowerShell 中，先用 [Microsoft 的 .NET 安装脚本](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)将 .NET 10 SDK 安装到仓库本地的 `.tools/dotnet`：

```powershell
New-Item -ItemType Directory -Force .tools | Out-Null
Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile .tools/dotnet-install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .tools/dotnet-install.ps1 -Channel 10.0 -InstallDir .tools/dotnet
```

然后构建并运行自动测试：

```powershell
& .\.tools\dotnet\dotnet.exe restore InputTracker.sln
& .\.tools\dotnet\dotnet.exe build InputTracker.sln -c Release --no-restore
& .\.tools\dotnet\dotnet.exe test InputTracker.sln -c Release --no-build
& .\.tools\dotnet\dotnet.exe publish src\InputTracker.App\InputTracker.App.csproj -c Release -r win-x64 --self-contained true --no-restore
```

分阶段验收运行 `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -Stage Portable`。自动测试不能替代真实键盘、鼠标和 Windows 会话现场验收。

## 文档入口

[文档导航](docs/README.md)区分使用说明、现行实现契约、验收材料和历史记录，也说明 `.tools/publish/` 手动测试包的保留与清理规则。`.tools/` 是被 Git 忽略的本地工作目录，不属于源码版本。
