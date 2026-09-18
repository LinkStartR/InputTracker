# BLOCKED

## B-016：旧版本鼠标轨迹文件的物理删除需要用户单独确认

- 2026-09-07 用户要求完整移除鼠标轨迹功能；当前产品已停止采集、保存、查询和回放轨迹，并移除相关界面及公开接口。
- 已存在的 `.mspool` / `.mchunk` 属于用户历史数据。自动删除、覆盖或迁移属于不可逆操作，当前未获“删除历史数据”的明确授权，因此旧文件保持原样且运行时不读取。
- 这不阻塞新版本使用：鼠标只保存左、右、中、侧键一、侧键二的每日次数。若用户希望连历史文件也清除，必须另行明确确认目标绿色目录和可恢复方案后再执行。

## B-015：UI 第一切片完整 App.Tests 主机执行审批流断线

- 2026-08-22 `a1a16ba` 后的 UI 第一切片已冻结；主线程权威 `dotnet build tests\InputTracker.App.Tests\InputTracker.App.Tests.csproj -c Release --no-restore` exit 0，0 warnings / 0 errors，1.40 秒。
- 随后的完整 `dotnet test ... --no-build --no-restore` 主机执行在等待输出时被审批服务以响应流断开拒绝，未获得可采信的最终退出码。没有使用间接命令或高权限绕过。
- 改用权限更低的默认沙箱执行同一测试，raw exit 1，唯一错误为 `MSB4184`：访问 `C:\Users\Administrator\AppData\Local\Microsoft SDKs` 被拒绝；未进入测试程序集。子任务冻结前已有目标 7/0/0、旧完整 75/0/0，但新增两项后的预期 77 全套尚未由主线程权威执行。
- 影响：UI 第一切片不得提交或宣称完整回归通过；源码保持冻结。解除方式是用户在获知本次审批断线后再次允许仓库本地 `.tools/dotnet` 的 App.Tests test，随后主线程只重跑该冻结命令，不重做实现。
- 连续阻塞审计：该同一条件已跨三个连续目标轮次存在。期间所有不依赖执行权限的工作已完成并提交：完整目标审计、离线恢复 App 安全协议、鼠标回放 UI 协议、Storage fixed snapshot/cursor/seal-reason v1 协议。继续实现会扩大未验证代码面，已无可安全推进的独立实现项。
- 精确恢复命令：`& '.\.tools\dotnet\dotnet.exe' test tests\InputTracker.App.Tests\InputTracker.App.Tests.csproj -c Release --no-build --no-restore --logger 'console;verbosity=minimal'`。用户在看到本阻塞后明确授权即可解除；预期只需验证冻结的 77 项，不重新实现或修改测试。
- 2026-08-24 已解除：用户明确允许继续本地 build/test；冻结 UI 先以 77/0/0 转绿，视觉收敛后新增深色 ComboBox 主题回归，最终 App build exit 0、0 warnings / 0 errors，App.Tests 78 passed / 0 failed / 0 skipped。B-015 不再阻塞 UI 本地提交与复测；真实桌面缩放矩阵及领导视觉确认仍归 B-003。

## B-014：已验证 CI 修正无法推送到 GitHub（用户明确暂缓，不阻塞本地交付）

- 2026-08-22 本地提交 `d4b8a54 test: make dashboard background assertion deterministic` 已完成，提交前本地主机证据为 focused 10/10、App 70/0/0、solution 321/0/0、Portable 321/25/45 全绿；工作树提交后 clean，分支只比 origin ahead 1。
- 同一 `git push origin codex/ui-storage-integration` 连续三次 raw exit 1：第一次 40.3 秒后 `Recv failure: Connection was reset`；第二、三次各约 21 秒后 `Failed to connect to github.com port 443`。没有 force push、没有改远端、没有绕过网络限制。
- 影响：Draft PR #3 仍停在远端 `b62e0b8`，GitHub CI 尚未看到 `d4b8a54`，因此不能宣称远端 CI 已恢复。产品本地构建和首次复测包不受影响。
- 2026-08-22 用户明确指示“不用管 GitHub，本地能运行即可”。因此当前不再重试 push，B-014 不阻塞本地人工复测；若未来恢复远端同步，只需推送现有本地提交并等待 PR #3 CI，禁止重做已通过实现或降低测试。

## B-013：CI 测试修正后的本地主机验证审批流断开（已解除）

- 2026-08-22 Draft PR #3 的两个 Windows CI 均在同一 App 测试失败：xUnit 在线程池线程执行测试，旧断言把 `Task.Run` 合法复用同一线程误判为 UI 阻塞；两次分别为 69 passed / 1 failed / 0 skipped，Input 38/0/0、Acceptance 25/0/0、Storage 188/0/0。
- 已做未提交窄修：测试从专用非线程池调用线程启动百万点刷新，并同时断言枚举只在线程池执行；产品 `src/**`、百万点计数、内存上限和验收阈值均未修改。
- 2026-08-22 用户已明确允许运行仓库本地 .NET build/test。App tests build exit 0（0 warnings / 0 errors）；失败用例连续 10 次均为 1 passed / 0 failed / 0 skipped；App 全套 70/0/0；solution 全套 321/0/0；`scripts/verify.ps1 -Stage Portable` exit 0，321 tests、25 Acceptance、45 gates，证据 `.tools/verification/20260822-112041-564/verification.json`。
- 本地审批阻塞已解除；剩余动作是提交、推送并等待 GitHub CI 复验，因此不能在 CI 变绿前把远端状态写成已通过。

## B-009：GitHub HTTPS 不可达，Storage 分支尚未 push / draft PR

- 2026-08-09 在 `codex/storage-stats@40067f2`、push 前工作树 clean、`gh 2.93.0` 且账号 `HorizonZhi` 已认证的前提下，三次推送同一分支均失败；当前未提交内容仅为本阻塞记录与对应进度记录。
- 第一次 `git push -u origin codex/storage-stats` raw exit 1，54.4 秒后报 `Recv failure: Connection was reset`；第二次同命令 raw exit 1，21.4 秒后报无法连接 `github.com:443`；第三次仅对单命令指定 HTTP/1.1，raw exit 1，21.4 秒后仍无法连接 `github.com:443`。
- 影响：本地 7 笔 Storage 提交完整保留且已验证，但远端分支和 draft PR 尚未创建；禁止伪报 GitHub 同步完成。
- 处置：停止继续刷网络，先做不依赖远端的 Storage 写入/查询切片；外网恢复后从同一 clean commit 重试 push，再按 `github:yeet` 流程创建 draft PR。该阻断不影响本地实现与测试。
- 2026-08-13 状态更新：网络恢复；`git push -u origin codex/ui-storage-integration` exit 0，创建远端分支；`gh pr create --draft --base main --head codex/ui-storage-integration` exit 0，draft PR #3：`https://github.com/LinkStartR/InputTracker/pull/3`。该 PR 明确为依赖栈、当前不可直接合并。B-009 对当前分支已解除；早期失败证据保留用于审计。

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
- 2026-09-07 用户视觉复测：浅色侧栏、霞鹜文楷字体、深浅切换和退出回归通过；文案未通过，且部分小字号不适配霞鹜文楷、阅读不舒服。用户明确要求先记录并跳过，继续功能；因此这两项仍阻塞最终视觉确认，但不阻塞鼠标按键统计。
- 2026-09-17 更新：正文已改为 Microsoft YaHei UI，最低字号提高到 12，用户确认字体版 `ok`；文案改写后评价为“勉勉强强”，可继续使用但不算强视觉确认。完整 1440×900 / 1920×1080、100% / 150%、浅色 / 深色截图矩阵仍未完成，B-003 继续阻塞最终发布完成声明。

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

## B-008：过渡 mouse spool 的等价校验与清理决策

- 当前状态：正式 chunk 与已封口 CRC spool 可能同时保存同一批原始鼠标事件；本切片只新增无损编码、原子 catalog 与恢复，不删除、移动、覆盖任何 spool、chunk、tmp 或损坏文件。
- 风险：若只凭 catalog 水位删除 spool，可能掩盖字段映射、范围、CRC/hash 或恢复差异；若长期保留全部副本，磁盘增长不可控。
- 解除门槛：后续独立切片必须逐记录证明 spool 与已 durable chunk 在 device、calibration segment、sequence、monotonic timestamp、motion/virtual desktop、坐标、完整按钮位和双向 signed wheel 上完全等价，并证明 catalog 水位已提交且不存在 integrity fault。
- 不可逆停点：任何清理前必须形成明确的保留期/范围/用户确认与断电恢复策略，并通过删除前后恢复测试和 C2 Acceptance seam。未达到门槛时只能报告重复占用，不得自动清理。

## B-010：设备身份自动 merge / split 暂缓

- 当前实现：Storage 在接口指纹未变且硬件指纹一致时复用；具备 serial 且非 Bluetooth/RDP/virtual 的强身份可跨接口稳定复用。2026-09-07 按用户明确要求增加保守的重连记忆：物理分类完整、ContainerId 存在、HardwareIds 非空且候选唯一时允许跨接口复用，但标记为 `ReconnectableHardware` / “非永久唯一”，不冒充强身份。
- 安全边界：Bluetooth、RDP、virtual、分类不完整、无 ContainerId、硬件信息变化均不跨接口复用；若同一哈希已对应多个历史设备，则所有候选标记 `MultipleCandidates` 并新建独立设备，不自动合并或猜测归属。
- 阻塞原因：两个历史 GUID 的 merge 会重写既有键盘聚合、mouse spool/chunk/catalog 外键与查询语义；split 同样需要可审计的记录归属规则。当前没有真实多设备证据、用户确认或可逆重写协议。
- 当前处置：强身份若已映射到多个设备则 fail closed；不自动 merge、split、删除、移动或覆盖任何用户 spool/chunk/tmp/corrupt 文件。
- 解除门槛：独立迁移设计、冲突预览、备份/回滚、逐记录归属证明、真实重复设备/重插测试及 C2 Acceptance seam 全部通过后，才可在用户明确确认下实施。

## B-011：键盘、鼠标按键及二者合并的完整日期范围删除已实现；日内部分范围继续阻断

- 2026-09-18 用户要求删除对象可以选择键盘、鼠标或全部。当前产品可删除明确选择设备的键盘日统计、鼠标按键日统计，或在同一日期范围内合并删除当前键盘和当前鼠标；鼠标轨迹功能仍完整移除，不删除或恢复移动轨迹。
- 已实现：预览逐行按保存的 Windows 时区解析 `capture_local_date`；只有所有相交日桶都完整落在所选 `[start,end)` 内才允许执行。预览指纹包含目标行身份、次数、时长与 durable ordinals；状态变化、错误 token、空范围、半日边界或混合时区部分相交均 fail closed。
- 数据安全：执行要求用户已明确暂停且 Storage 已排空；先在程序目录创建完整 `.itbackup`，再用单个 SQLite 事务删除所选设备的精确日桶，并在 commit 前核对删除行数。“全部”同样只创建一份备份并在一个事务中提交键盘与鼠标两类删除，避免只成功一半。备份失败或事务失败不宣称成功。
- 仍受限：键盘与鼠标按键都只保存 capture-local daily 聚合，无法从中精确拆出某天内部的任意小时/分钟。当前 UI 提供今天/7/14/30 天快捷项及日历起止日期，日期两端都包含；若未来增加日内时间选择，必须先升级聚合格式，禁止按比例扣减。
- 验收缺口：固定归属的 `tests/Acceptance/**` 与 `scripts/verify.ps1` 仍保留旧的“执行一律阻断”阶段 gate，本功能分支未擅自修改。最终统一判卷前须由其维护范围增加“备份→删除→恢复”、stale token、DST 23/25 小时与部分日阻断 gate，且测试数只能增加。
# B-012：Codex 额度曾阻止主机权限下的 Windows SDK 编译验证（已解除）

- 2026-08-13 在 `codex/ui-storage-integration@f5421aa` 上执行 `.\.tools\dotnet\dotnet.exe build tests\Acceptance\InputTracker.AcceptanceTests.csproj -c Release --no-restore`，沙箱内 exit 1、0 warnings / 1 error；唯一错误为 `MSB4184`，读取 `C:\Users\Administrator\AppData\Local\Microsoft SDKs` 被拒绝，尚未进入新增源码编译。
- 按权限规则请求相同命令在主机权限下执行，审批系统因 Codex 用量额度耗尽而拒绝，并提示最早可在 2026-08-20 14:43 后重试；禁止通过间接命令或其他路径绕过。
- 影响：可以继续静态实现、审阅和记录沙箱红证据，但在主机验证能力恢复前，不能可信生成并宣称最新版绿色测试包，也不能把沙箱权限错误记作产品测试失败。
- 处置：继续所有不依赖主机权限的工作；每个实现切片保持未验证状态。额度恢复后从冻结 commit 依次执行 restore、build、test、verify 红→绿与 publish，不重做已完成实现。
- 2026-08-22 状态更新：主机权限验证能力已恢复。仓库本地 SDK `10.0.302` 下 restore exit 0、solution Release build exit 0（0 warnings / 0 errors）、solution tests 321 passed / 0 failed / 0 skipped、`scripts/verify.ps1 -Stage Storage` exit 0（39 gates）、self-contained publish 与 `--self-check` exit 0。B-012 已解除；历史权限失败证据保留审计，不再阻塞当前工作。
