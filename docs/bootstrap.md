# Bootstrap 技术说明

当前阶段只提供 WPF 安全外壳，不接收或保存任何键盘、鼠标输入。

- 目标框架：`.NET 10` / `net10.0-windows10.0.19041.0` / `win-x64`。
- 权限：manifest 固定 `asInvoker`，不请求管理员权限。
- 绿色目录：启动时只探测 `AppContext.BaseDirectory`；不可写时明确拒绝采集，不回退到 AppData、Temp 或注册表。
- 单实例：按标准化程序目录计算 Windows `Local` 命名 Mutex；同一绿色目录只允许一个实例。
- 托盘：使用框架自带 `NotifyIcon`；用户可查看并切换暂停/恢复状态，或明确退出。
- 诚实状态：bootstrap 尚无 Raw Input，恢复后显示“未采集”，不伪称正在采集。
- 发布：`dotnet publish InputTracker.sln -c Release -r win-x64 --self-contained true`。
- 构建产物：`bin/obj/publish` 统一重定向到被 Git 忽略的 `.tools/build` 与 `.tools/obj`。
- 自动烟测：发布产物支持 `--self-check`（目录探针）和 `--hold-smoke`（持有单实例 5 秒）；仅用于无界面验收，不启动采集。

所有运行数据、配置、日志和备份的后续实现必须继续以程序目录为根。唯一允许的目录外写入是用户主动启用的 Startup 快捷方式，该功能尚未实现。
