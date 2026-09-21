# InputTracker 验收计划

## 自动化门槛

每个可提交阶段在本地执行 `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -Stage <阶段名>` 并保存退出码和摘要。脚本使用仓库本地 `.tools/dotnet`，依次完成 restore、Release build、test（要求 skipped = 0）、win-x64 self-contained publish 和阶段 gate；当前 CI 只运行其独立的 restore/build/test/publish 流程，不代替这份分阶段证据。

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

分别临时破坏聚合期望、禁止鼠标轨迹产物规则、绿色路径规则，保存非零退出码和关键失败输出；恢复原状后运行全套命令并保存全绿输出。临时破坏不得提交。

## 真实硬件与性能

- 记录 CPU、内存、Windows 版本、输入设备接口标识与硬件信息。
- 两键盘两鼠标在前台/后台连续采集不少于 2 小时，逐设备统计零串线。
- 1000Hz 鼠标连续 30 分钟，后台平均 CPU 不高于 3%，工作集不高于 200MB，UI 响应不高于 200ms。
- 报告每小时磁盘增长、入队/落盘/恢复计数，不得抽样或静默丢点。

## Storage 阶段

执行 `scripts/verify.ps1 -Stage Storage`。该阶段必须完整回归 Bootstrap 与 InputCore gate，并通过日/周/月/年半开区间、Pacific 23/25 小时 DST、显式 Windows 时区、CSV 隐私列、PNG、版本化迁移、设备映射、鼠标按键汇总且不产生轨迹文件、备份恢复和恢复日志测试。TRX 使用精确测试名并要求每项为 `Passed`，缺失、改名或非通过均失败。

当前格式只能精确删除完整的本地统计日；切入统计日中间、混合时区或没有匹配记录的范围必须报告 `BlockedByCurrentFormat`，不得估算或修改数据。完整日删除也必须在用户暂停、Storage 已停止接收写入、预览 token 仍有效、最终确认和程序目录内预删除备份均满足后执行。`deletion-preview-and-execution-blocked` 保留为兼容 gate ID，验证完整日删除受保护执行，以及不精确范围的 fail-closed 行为；不得把阻断范围称为删除通过。真实多设备、1000Hz、磁盘增长和物理断电保持 `not-run`，按 Storage 人工清单与 schema 现场验收。

`-InjectStorageChunkCorruption` 只破坏 `.tools` 下验收临时块；`-InjectStorageAggregateMismatch` 只改变 Acceptance 期望。二者互斥且只允许 Storage/Portable，原验证进程必须退出 1 并记录 mutation id。注入环境必须在 `finally` 恢复，不得接触产品或用户数据。

## Portable 阶段

执行 `scripts/verify.ps1 -Stage Portable`。该阶段必须完整回归 Bootstrap、InputCore 与 Storage gate，并锁定首次采集授权、程序目录内设置、Startup 自有快捷方式的精确创建/删除、外来快捷方式保护、用户/锁屏/挂起暂停原因叠加、同步 barrier、受控注销/关机和 win-x64 self-contained 中文空格路径。

Startup 真实创建/清理、锁屏与挂起、普通用户/UAC、运行时无网络、Windows 10/11、真实硬件和视觉矩阵均保持 `not-run`，`releaseEligible=false`；按 Portable 人工清单和 schema 记录证据，自动化不得冒充现场通过。

### 离线恢复增量门槛

Portable 判卷还必须验证唯一公开 `RestoreOffline` 门面、活动 Storage owner fail-closed、一次性 A/B/C store 精确往返、automatic pre-restore backup、路径与 reparse 边界、App 用户暂停/二次确认/精确 teardown/终态退出契约，以及完整发布目录的中文空格可丢弃副本自检。新增 Acceptance 恢复只发生在 `.tools/acceptance` 下全新临时目录；普通 Storage 单元测试只使用自动销毁的隔离临时目录。两者均不得接触现有发布数据或用户 store。

App 生命周期自动证据由普通 App 测试锁定真实 internal seam，Acceptance 仅用源码契约补充跨程序集判定；它不能替代真实 WPF 点击、SessionEnding 或恢复后人工重启。人工流程固定在 `tests/Acceptance/Manual/offline-restore-checklist.md`，状态保持 `not-run`、`releaseEligible=false`，直到用户在完整绿色目录的可丢弃副本中完成两次恢复往返。

## 视觉矩阵

在 1440×900 与 1920×1080、100% 与 150% 缩放、浅色与深色下截图；核对无截断、重叠、不可读文字，普通文字对比度至少 4.5:1，热力色阶不遮字。最终由领导确认。

## 数据与便携性

验证数据、配置、日志、备份均位于程序目录；唯一例外是用户主动启用的 Startup 快捷方式，关闭后删除。不写注册表，不请求管理员权限，不安装服务，不遥测，不做运行时联网。

