# InputTracker 开发约定

- 这是 Windows 10/11 x64 的本地绿色版应用。源码构建和测试命令见 [README.md](README.md)，当前使用与验收边界见 [docs/README.md](docs/README.md)。
- 当前 App 只持久化键盘物理键聚合和鼠标按键次数；隐私与持久化契约见 `docs/input/`、`docs/storage/`。
- Storage 的鼠标 X1/X2 保留 Raw Input 原始编号。界面约定是鼠标图上方标 X1、下方标 X2；`DashboardViewModel.ToMouseButtonCounts` 将原始 X2 映射到显示 X1、原始 X1 映射到显示 X2。总览与鼠标页共用该结果，修改时核对两处及测试。
- `.tools/publish/` 的旧包可能包含独有的 `data/`、设置或备份。清理前按 [docs/README.md](docs/README.md) 核对，不能把运行数据当作可重建的发布文件。
- 自动测试通过不等于真实硬件、Windows 生命周期和视觉矩阵现场验收完成；发布边界见 [docs/acceptance-plan.md](docs/acceptance-plan.md)。
