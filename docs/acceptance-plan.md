# InputTracker 验收计划

## 自动化门槛

每个可提交阶段统一执行并保存退出码和摘要：

1. `dotnet restore`
2. `dotnet build -c Release`
3. `dotnet test -c Release --no-build`，要求 skipped = 0
4. `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1`
5. `dotnet publish -c Release -r win-x64 --self-contained true`

验收用例至少覆盖日、周、月、年及时区边界，设备隔离，重插映射，隐私 schema，崩溃恢复，范围删除，版本化迁移，绿色路径和运行时无网络。

建立 1,000,000 事件管线测试，要求丢失 0、顺序错误 0；测试数只增加，不允许 skip/todo、删除测试、放宽断言、吞异常或修改阈值来过关。

## Bootstrap 阶段

执行 `scripts/verify.ps1 -Stage Bootstrap`。该阶段必须使用仓库本地 .NET 10 SDK，并验证：

- Release 构建与 win-x64 self-contained 发布成功，所有构建和验证证据位于被忽略的 `.tools`。
- TRX 中测试总数不少于基线，失败数和跳过数均为 0，Bootstrap gate 不少于基线且必需 gate 无缺失。
- manifest 使用 `asInvoker` 且 `uiAccess=false`，绿色输出不落入源码目录的 `bin` 或 `obj`。
- 发布产物在带中文和空格的可写目录执行 `--self-check` 返回 0，不产生程序目录外运行写入。
- 同目录 `--hold-smoke` 持有实例期间，第二个 `--self-check` 返回 2；两个进程最终均退出，无残留进程。

`-InjectedDataPath` 只用于安全反向验证。传入发布目录外路径时，验证器必须明确失败并写入非零结果；它不得修改产品代码或当前工作树。

普通用户、UAC、可视托盘暂停和恢复按 `tests/Acceptance/Manual/bootstrap-checklist.md` 人工验收。真实多设备、性能、统计、恢复和最终视觉在对应后续阶段到达前保持未验，不得因 Bootstrap 全绿而标记为通过。

## Input Core 阶段

执行 `scripts/verify.ps1 -Stage InputCore`。它必须完整回归 Bootstrap gate，并额外验证生产 `BoundedInputQueue` 与 `InputWorker` 的 1,000,000 事件计数：`produced`、`accepted`、`dequeued`、`sinkAcknowledged` 均为 1,000,000，且 `overflow`、`parseErrors`、`outOfOrder` 均为 0。

队列溢出与 sink 异常必须关闭入口、注销 Raw Input 并进入 `IntegrityFault`；暂停原因集合必须只有在最后一个原因解除后才能恢复，且 barrier 前的已接受事件均已消费并确认。键盘公共 schema 仅允许运行时设备 ID、物理扫描码、E0/E1、Down/Up 与聚合 timestamp；`VKey` 与 `Message` 只允许存在于 `RawInputInterop.cs` 的私有 Win32 布局，不能映射到 parser、公共 DTO 或产品调用。鼠标必须保留相对/绝对 dx/dy、按钮变化、垂直/水平滚轮和有符号数据；设备句柄复用必须生成新 arrival generation 和 RuntimeDeviceId。

InputCore 自动测试仅验证规范化注入和生产管线，不是物理硬件终验。`tests/Acceptance/Manual/input-core-hardware-runbook.md` 和性能 schema 必须存在；证据必须明确 `hardwareTerminalStatus=not-run`、`releaseEligible=false`，直到完成两键盘两鼠标 2 小时与 1000Hz 鼠标 30 分钟现场验收。

## 反向验证

分别临时破坏聚合期望、轨迹块校验、绿色路径规则，保存非零退出码和关键失败输出；恢复原状后运行全套命令并保存全绿输出。临时破坏不得提交。

## 真实硬件与性能

- 记录 CPU、内存、Windows 版本、输入设备接口标识与硬件信息。
- 两键盘两鼠标在前台/后台连续采集不少于 2 小时，逐设备统计零串线。
- 1000Hz 鼠标连续 30 分钟，后台平均 CPU 不高于 3%，工作集不高于 200MB，UI 响应不高于 200ms。
- 报告每小时磁盘增长、入队/落盘/恢复计数，不得抽样或静默丢点。

## 视觉矩阵

在 1440×900 与 1920×1080、100% 与 150% 缩放、浅色与深色下截图；核对无截断、重叠、不可读文字，普通文字对比度至少 4.5:1，热力色阶不遮字。最终由领导确认。

## 数据与便携性

验证数据、配置、日志、备份均位于程序目录；唯一例外是用户主动启用的 Startup 快捷方式，关闭后删除。不写注册表，不请求管理员权限，不安装服务，不遥测，不做运行时联网。

