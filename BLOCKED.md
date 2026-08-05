# BLOCKED

## B-001：指定的 `main-child-workflow` Skill 不可用

- 证据：可用 Skill 清单无此项；递归检查 `C:\Users\Administrator\.codex\skills` 亦无同名目录。
- 影响：无法逐字执行指定 Skill。
- 处置：使用已安装的 `large-project-orchestrator`，其线程可见性、索引、固定 Git/测试角色和汇报规则覆盖所需流程；不阻塞产品工作。

## B-002：需要新增仓库本地 .NET 10 SDK（已解除）

- 证据：`dotnet --info` 仅列出 `9.0.305`；任务指定 .NET 10。
- 权限边界：只允许微软官方安装脚本写入被 Git 忽略的 `.tools/dotnet`，不修改系统环境。
- 状态：已用微软官方脚本安装 `10.0.302` 到 `.tools/dotnet`；未写 PATH，`.tools/dotnet/test.txt` 经 `git check-ignore -v` 命中 `.tools/.gitignore:1:*`。

## B-003：最终真实硬件与视觉验收需要现场参与

- 需要：真实两键盘两鼠标前后台连续采集不少于 2 小时、1000Hz 鼠标 30 分钟，以及领导逐张确认界面截图。
- 限制：mock 不可替代，自动执行者不能伪造物理硬件配置或领导确认。
- 高频边界：当前 Input Core 对每个 `WM_INPUT` 使用 `GetRawInputData` 逐消息读取，尚未实现 `GetRawInputBuffer` buffered drain；在真实 1000Hz 鼠标 30 分钟验收通过前，不得声称 1000Hz 完整性或性能已经验证。
- 决策门槛：由真实 1000Hz 验收的 queue、CPU、overflow、parse error 与顺序诊断决定是否加入 buffered drain，不以百万个注入事件的普通测试替代硬件结论。
- 处置：先完成自动化、采集证据脚本和可复现人工步骤；到达门槛时提交实际结果或保持阻塞。

## B-004：任务指定的 NuGet 依赖（已获任务书授权，待逐项落锁）

- bootstrap 预计需要：`Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`；WPF 与托盘优先使用框架自带能力。
- storage-stats 预计需要：`Microsoft.Data.Sqlite`；压缩优先使用 BCL，避免额外依赖。
- 约束：必须锁定明确版本并进入 restore/build/test；任何未列出的第三方包先追加到本文件，不静默引入。

## B-005：Bootstrap 普通用户可视启动、UAC 与截图证据

- 已通过：非管理员 `CodexSandboxOffline` 从中文空格绿色目录执行 `--self-check` 退出 0；manifest 为 `asInvoker` / `uiAccess=false`；受控桌面真实 UI Automation 验证窗口可见、暂停/恢复状态和托盘 tooltip 同步，退出后残留进程 0。
- 限制：权限规则要求启动 GUI 时使用受控执行，该桌面调用者实测为 `Administrator`；默认非管理员沙箱不允许直接打开 GUI。隔离桌面的全屏截图为黑屏，WPF `PrintWindow` 仅得到空白客户区，不能作为视觉证据。
- 影响：尚不能声称普通用户完整可视流程“不弹 UAC”或提交可读截图；自动门槛、CI、单实例和状态控制链不受影响。
- 处置：该缺口不阻塞已审查且 CI 全绿的 Bootstrap 代码经 PR 合并，但始终阻塞最终/现场验收与发布完成声明；保留 `tests/Acceptance/Manual/bootstrap-checklist.md`，等待可操作的普通用户桌面现场补拍，不以管理员截图或 mock 冒充。

## B-006：Storage 的 durable acknowledgement 语义（契约层已解除）

- 证据：当前 `IInputEventSink.WriteAsync` 为逐事件串行确认，Input Core 在返回后增加 `SinkAcknowledged`；Storage 设计同时要求 1000Hz 低开销、崩溃可恢复和不丢原始鼠标轨迹。
- 不确定点：若把每个 `SinkAcknowledged` 定义为已经抵抗突然断电的逐事件 `fsync`，现有单事件接口可能无法满足 CPU/延迟门槛；若仅在块或暂停/移除/停止 boundary 强制落盘，则必须明确进程异常退出与突然断电的不同保证，不能伪称逐事件耐久。
- 尖峰证据：真实文件系统/SQLite 运行覆盖 A/B/C 的 100,000 mixed 与 C 的 30,000 mouse 等价负载，最终 4m25s 退出码 0；四组均满足 accepted=sinkAcknowledged=sqliteCommitted=powerDurable=recovered，lost=0、corrupt=0。A 仅 379.99 events/s，B 为 101442.24 events/s，C mixed 为 113509.86 events/s。
- 定案：`SinkAcknowledged` 仅表示进入当前聚合事务或完整追加 CRC spool，不等于 fsync；键盘选 B 有界批事务，鼠标选 C spool，并且只在 `Flush(true)` 后提交 SQLite 目录水位，二者都完成后才推进 `PowerDurable`。
- 未解除范围：物理突然断电与真实 1000Hz/30分钟硬件仍为 `not-run`，`releaseEligible=false`；不得用本契约层尖峰冒充终验。

## B-007：native SQLite 3.49.1 命中 WAL-reset 已知风险范围

- 证据：锁版依赖为 `Microsoft.Data.Sqlite` / `Microsoft.Data.Sqlite.Core 10.0.10` 与 `SQLitePCLRaw 2.1.11`；尖峰实际执行 `SELECT sqlite_version()` 返回 `3.49.1`。
- 风险：SQLite 官方说明 WAL-reset corruption bug 影响 3.7.0–3.51.2，修复于 3.51.3+，并回移到 3.44.6 / 3.50.7。`3.49.1` 不在修复线，不能声称风险消失。
- 当前规避：产品坚持唯一 writer/checkpointer 连接且 `Pooling=false`，避开已知的“至少两个连接在不同线程/进程同时写或 checkpoint”触发前提；这只是规避触发条件，不是修复 native 库。
- 阻塞范围：在生产发布或引入第二连接/外部 checkpoint 前，必须获得任务授权升级到命中官方修复线的 native 依赖并重跑迁移/恢复/尖峰，或形成明确的审查风险接受决定；本里程碑不得静默更换已授权包。
