# InputTracker 执行进度

## 目标、顺序、最大风险（8 行）
1. 目标：交付 Windows 10/11 x64 本地绿色版 InputTracker，用户始终知情并可暂停、删除、备份和恢复。
2. 隐私：键盘仅保留设备、物理键、日期的次数与时长；不保存文字、顺序、窗口或进程。
3. 数据：鼠标原始轨迹逐设备完整保存，分块增量编码压缩，不以抽样冒充原始数据。
4. 顺序：任务 0 → bootstrap → input-core → storage-stats → ui → portable → 全量验收。
5. Git：每项从最新 main 开 `codex/*`，独立提交、推送和 draft PR，前置 PR 合并后再继续后继分支。
6. 最大风险：Raw Input 设备身份不稳定、回调背压与不丢事件、崩溃恢复和时区边界正确性。
7. 外部验收风险：两键盘两鼠标 2 小时、1000Hz 鼠标 30 分钟及领导视觉确认无法纯自动化替代。
8. 让步顺序：隐私与数据正确 > 不丢事件 > 稳定和可恢复 > 功能完整 > 视觉 > 速度。

## 2026-08-05 任务 0 基线

- 分支：`main`，跟踪 `origin/main`，工作树干净。
- 提交：`82acb1d Initial commit`。
- 命令：`git status --short --branch` → `## main...origin/main`，退出码 0。
- 命令：`git branch -a` → 本地 `main`、`origin/main`，退出码 0。
- 命令：`git log -1 --oneline` → `82acb1d Initial commit`，退出码 0。
- 命令：`git ls-files` → `LICENSE`、`README.md`，退出码 0。
- 命令：`dotnet --info` → SDK `9.0.305`，Host `9.0.9`，RID `win-x64`，退出码 0。
- 远端核验：公开仓库，默认 `main`，仅 1 分支、1 提交、0 PR；与任务书数字一致。
- 克隆：沙箱内首次失败（GitHub 443 不可达）；经受控网络许可执行指定 `git clone ... .` 后退出码 0。
- 编排：采用 `large-project-orchestrator` 完整模式；指定的 `main-child-workflow` 未安装，等价替代理由见 `BLOCKED.md`。
- 结果：基线核验完成；尚未开始产品代码。
- 下一步：创建并确认子任务可定位，安装仓库本地 .NET 10 SDK，再建立 bootstrap 分支。

## 2026-08-05 子任务可定位性

- 分支：`main`；提交：尚无新提交。
- 命令/工具：创建 7 个项目 worktree 线程并用线程列表按标题查询。
- 输出：固定角色与 5 个功能子任务均返回唯一线程 ID，host=`local`，状态=`active`。
- 结果：标题已设为任务名，全部可定位；关键工作下发门槛通过。
- 下一步：安装仓库本地 .NET 10 SDK，创建 `codex/bootstrap`，再给 Bootstrap/C2/C1 下发任务卡。

## 2026-08-05 仓库本地 .NET 10

- 分支：`main`；提交：尚无新提交。
- 命令：微软官方 `dotnet-install.ps1 -Channel 10.0 -Quality GA -Architecture x64 -InstallDir .tools/dotnet -NoPath`。
- 首次输出：系统执行策略拒绝脚本，退出码 1；未写入 SDK。
- 第二次输出：仅对子进程加 `-ExecutionPolicy Bypass`，下载与本地包大小均为 `297545270` 字节，安装 `10.0.302`，退出码 0。
- Git 忽略：`git check-ignore -v .tools/dotnet/test.txt` → `.tools/.gitignore:1:*`，退出码 0；`.tools` 未出现在 `git status --short`。
- 结果：.NET 10 工具链位于仓库目录，未改系统 PATH 或全局 SDK。
- 下一步：用 `.tools/dotnet/dotnet.exe --info` 复核，建立 `codex/bootstrap` 并提交任务 0 文档。

## 2026-08-05 Bootstrap 分支建立

- 分支：`codex/bootstrap`；提交：尚无新提交。
- 首次命令：`git switch -c codex/bootstrap` → 无法创建 `.git/refs/heads/codex/bootstrap`，退出码 1；refs 检查未发现同名冲突。
- 第二次命令：经仓库 Git 元数据写权限许可重试同一命令 → `Switched to a new branch 'codex/bootstrap'`，退出码 0。
- 结果：未删除或改写任何 refs，任务 1 最终分支已从 `main@82acb1d` 建立。
- 下一步：固定角色检查意图文件；Bootstrap 子任务在独立 worker 分支实现，主任务审查后集成。

## 2026-08-05 首批任务卡

- 分支：`codex/bootstrap`；提交：尚无新提交。
- 工具输出：向 C1/C2/C3 发送任务均返回对应线程 ID，无错误。
- 结果：C1 只读检查 Git 意图文件；C2 只读审阅验收；C3 在 `codex/bootstrap-work` 实现任务 1，禁止修改判卷文件或推送。
- 下一步：收集 C1 报告并提交任务 0 文档；随后审查 C2/C3 报告并集成。

## 2026-08-05 任务 0 完成

- 分支：`codex/bootstrap`。
- 提交：`6fb492b docs: establish execution baseline`。
- 暂存检查：仅 `BLOCKED.md`、`PROGRESS.md`、`docs/acceptance-plan.md`、`docs/codex/thread-index.md`；4 文件、143 行新增，无白名单外文件。
- 命令输出：`git commit` → `[codex/bootstrap 6fb492b]`，退出码 0。
- 结果：远端/本地/SDK 基线、目标顺序风险、阻塞、线程索引、验收计划和仓库本地 .NET 10 均已建立；任务 0 完成。
- 下一步：集成 C3 的 bootstrap 实现，由 C2 建立不可弱化的验收脚本，再执行统一命令。

## 2026-08-05 C1 任务 0 Git 回报

- 分支/提交：`codex/bootstrap@6fb492b`。
- 输出：C1 只读复核确认该提交恰含 4 个任务 0 文档；无无关文件，`.tools` 命中忽略规则，`LICENSE` 未修改。
- 风险：共享主工作区在 C1 检查期间发生预期提交竞态；最终快照仅余本文件的完成记录未提交，LF/CRLF 有 Git 提示。
- 结果：提交边界和消息经独立角色核验通过，C1 恢复常驻。
- 下一步：在 bootstrap 功能提交前再次请求 C1 核验 staged 文件。

## 2026-08-05 C2 Bootstrap 验收审阅

- 分支/提交：主工作区 `codex/bootstrap@6fb492b`；C2 本轮只读，无提交。
- 结论：现有计划保留最终门槛但缺阶段判卷；bootstrap 不能宣称硬件、存储、UI 或 portable 已通过。
- 必要门槛：强制仓库本地 .NET 10、TRX `total>0/failed=0/skipped=0`、自包含发布、带空格/中文路径、目录 fail-closed、单实例、托盘状态、绿色边界与证据 JSON。
- 反向验证：bootstrap 仅做绿色路径判卷器红→绿；聚合与轨迹块破坏等待对应模块，不制造假证据。
- 结果：验收文件规划已明确，仍保持 `tests/Acceptance/**` 与 `scripts/verify.ps1` 由 C2 独占。
- 下一步：等 C3 回报 solution、进程、单实例、托盘与目录 API 契约后，C2 创建阶段化验收项目和脚本。

## 2026-08-05 Bootstrap build 第一轮（三次失败，切换验收项）

- 分支/提交：`codex/bootstrap@6fb492b` 加未提交实现。
- 第 1 次：沙箱拒绝读取 `AppData/Local/Microsoft SDKs`，`MSB4184`，退出码 1；非代码失败。
- 第 2 次：进入编译后报 WPF/WinForms `MessageBox` 歧义及缺少 `System.IO`，12 errors，退出码 1；已做最小显式限定。
- 第 3 次：应用 `InputTracker.dll` 编译成功；测试项目缺少 `global using Xunit`，12 个 `Fact` 解析错误，退出码 1。
- 处置：补入测试项目全局 using；遵守三败规则，不立即第四次 build，转做静态白名单/隐私/权限检查。
- 下一步：静态检查完成后开启统一命令第二轮，不放宽断言或验收阈值。

## 2026-08-05 Bootstrap 静态边界检查

- 第 1 次：误扫 `bin/Release` 自包含框架 DLL，二进制字符串污染输出并超时，退出码 124；未据此作代码结论。
- 第 2 次：限定 14 个 `.cs/.xaml/.csproj` 源文件并排除 `bin/obj`，退出码 0。
- 输出：禁止 API 命中 0；manifest 为 `asInvoker`；NuGet 仅 3 个已登记锁版本包；独占判卷文件不存在；白名单外路径 0；`.tools` 为 ignored。
- 结果：静态隐私、权限、依赖和文件边界通过。
- 下一步：回到 Release build 第二轮，随后 test、publish；C2 判卷脚本待入口契约稳定后添加。

## 2026-08-05 Bootstrap 初稿自动验证

- 分支/提交：`codex/bootstrap@6fb492b` 加未提交实现。
- restore：本地 SDK `10.0.302` 恢复 2 项目，退出码 0。
- build 第二轮：应用与测试项目成功，0 warnings / 0 errors，退出码 0。
- test：沙箱内首次因 Windows SDK 探测权限退出 1；受控重试后 6 passed、0 failed、0 skipped，退出码 0。
- publish：任务书原命令参数 `-c Release -r win-x64 --self-contained true`，生成 `src/InputTracker.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`，退出码 0。
- 实现：WPF 安全外壳、`asInvoker`、程序目录 fail-closed、同目录命名 Mutex、托盘暂停/恢复、诚实“未采集”状态、最小 CI；无 Raw Input/SQLite。
- 结果：代码级 restore/build/test/publish 通过；`scripts/verify.ps1`、验收项目、发布产物进程烟测和普通用户人工检查尚未完成。
- 下一步：C1 核验提交边界后提交该单元；C2 基于稳定入口创建判卷脚本与 bootstrap 验收。

## 2026-08-05 Bootstrap 绿色路径与进程烟测

- 输出布局：4 个已核对的本轮 `src/tests` 下 `bin/obj`（共 1091 个生成文件）已删除，可恢复；后续全部重定向到 ignored `.tools/build`、`.tools/obj`。
- 重定向后：restore 退出码 0；build 0 warnings / 0 errors；test 6 passed / 0 failed / 0 skipped；publish 退出码 0。
- 非管理员自检：调用者 `WIN-LV0RSKMPC24\CodexSandboxOffline`，`IS_ADMIN=False`，发布产物 `--self-check` 退出码 0。
- 单实例：第一实例 PID 14844 持有 5 秒并退出 0；第二实例 PID 12468 退出 2；结束后 `InputTracker` 残留进程 0。
- 路径：复制到 `.tools/smoke/绿色 路径` 后自检退出码 0；原发布与中文空格副本均命中 `.tools/.gitignore:1:*`。
- Git：`src/tests` 下无 `bin/obj`，`LICENSE` diff 为空。
- 结果：普通用户、自包含、中文空格路径、单实例和无残留进程的自动烟测通过；可视托盘人工检查仍未执行。
- 下一步：提交 bootstrap 功能单元，再由 C2 创建正式 TRX/路径/进程判卷与 `scripts/verify.ps1`。

## 2026-08-05 Bootstrap 功能单元提交

- 分支：`codex/bootstrap`。
- 提交：`2f8beb1 feat: add bootstrap Windows desktop shell`。
- staged 复核：23 个意图文件、727 行新增/4 行删除；`LICENSE`、`.tools`、`bin/obj`、`tests/Acceptance/**`、`scripts/verify.ps1` 命中 0。
- 命令输出：`git commit` → `[codex/bootstrap 2f8beb1]`，退出码 0。
- 结果：任务 1 功能实现成为独立可回滚提交；完整任务 1 仍等待 C2 判卷、反向验证、托盘人工检查、push 和 draft PR。
- 下一步：C2 从 `2f8beb1` 的 `codex/bootstrap-acceptance` worker 分支实现验收；主任务审查后集成。

## 2026-08-05 `.tools` 忽略规则持久化

- 分支：`codex/bootstrap`。
- 提交：`78606dc chore: persist local tools ignore rule`。
- 输出：仅 `.tools/.gitignore`、`docs/bootstrap.md`、`PROGRESS.md`、线程索引；14 行新增/2 行删除，`.tools` 其余内容命中忽略且未进入索引。
- 结果：新 clone 也会跟踪忽略规则本身，并忽略 SDK、build/obj/publish/verification/smoke 内容。
- 下一步：C2 worker 仍基于 `2f8beb1`；主任务集成其提交后自然保留本提交，不要求子任务重做。

## 2026-08-05 C2 Bootstrap 阶段判卷边界

- 分支/提交：主工作区 `codex/bootstrap@78606dc`；C2 只读审阅阶段未提交。
- 结论：现有 `docs/acceptance-plan.md` 是最终验收纲要，不足以证明 Bootstrap 之外的硬件、采集、存储、UI 或 portable 已通过。
- Bootstrap 门槛：强制仓库本地 .NET 10；解析 TRX 并要求 `total > 0`、`failed = 0`、`skipped = 0`；验证自包含发布、中文空格路径、程序目录 fail-closed、单实例、托盘状态控制链及无残留进程。
- 反向验证：本阶段只破坏绿色路径规则并验证判卷器明确失败；聚合期望和轨迹块校验等待对应模块落地，不制造伪证据。
- 未验：两键盘两鼠标、1000Hz 性能、四周期统计、轨迹恢复、完整视觉矩阵和领导确认继续保持 pending/B-003。
- 下一步：等待 C2 在 `codex/bootstrap-acceptance` 完成最小验收项目、基线、`scripts/verify.ps1`、实际验证和独立提交，再由主任务审查集成。

## 2026-08-05 Bootstrap 判卷集成与统一五命令

- 分支/提交：C2 `codex/bootstrap-acceptance@7c44525`；主分支 cherry-pick 后为 `codex/bootstrap@739e16a`。
- 文件边界：C2 提交仅 8 个白名单文件；新增 5 个无 skip 的 Bootstrap 验收测试、测试/gate 只增不减基线、阶段判卷器和人工清单。
- `restore`：仓库本地 SDK 恢复 3 个项目，退出码 0。
- `build -c Release`：3 个项目成功，0 warnings / 0 errors，退出码 0。
- `test -c Release --no-build`：普通测试 6 passed、验收测试 5 passed，failed=0、skipped=0，退出码 0。
- `scripts/verify.ps1 -Stage Bootstrap`：SDK 10.0.302；restore/build/TRX/publish/中文空格路径/单实例全部通过；`total=11`、`acceptance=5`、`failed=0`、`skipped=0`、`gates=11`，退出码 0。
- 绿证据：`.tools/verification/20260805-142005-654/verification.json`，状态 `passed`。
- `publish -c Release -r win-x64 --self-contained true`：产物在 `.tools/build/InputTracker.App/Release/net10.0-windows10.0.19041.0/win-x64/publish/`，退出码 0。
- 反向验证：`-InjectedDataPath C:\InputTracker-Outside-Should-Fail` 明确输出“越出程序目录，绿色路径反向验证按预期拒绝”，退出码 1；红证据 `.tools/verification/20260805-142143-692/verification.json`。
- 工作树检查：反向验证后仅 `PROGRESS.md` 与线程索引的进度更新；产品、测试和判卷器无额外修改，`git diff --check` 退出码 0。
- 未验证：普通用户可视托盘暂停/恢复、UAC 与截图仍按人工清单 pending；真实硬件、性能、存储、UI 和最终视觉仍为 B-003/后续阶段。
- 下一步：提交本进度单元，由 C1 最终复查；随后 push `codex/bootstrap` 并创建 draft PR，禁止直接推 `main`。

## 2026-08-05 Bootstrap 推送、draft PR 与受控桌面检查

- 分支/提交：`codex/bootstrap@e0b1c20`；`git push -u origin codex/bootstrap` 退出码 0，未直接推 `main`。
- PR：`https://github.com/LinkStartR/InputTracker/pull/1`，draft，base=`main`，head=`codex/bootstrap`；GitHub 报告 `MERGEABLE/CLEAN`。
- CI：PR #1 的两个 `windows` 运行均为 `SUCCESS`，用时 1m17s 与 1m9s。
- C1 只读复查：5 个提交边界清晰；`LICENSE` blob 未变；仅跟踪 `.tools/.gitignore`，无 `bin/obj` 或 `.tools` 运行产物；C2 独占文件归属正确。
- GUI 启动：受控桌面调用者 `Administrator`，`InputTracker` PID 16808，主窗口标题正确、`Responding=True`。
- 真实 UI 状态链：窗口 `未采集 → 已暂停 → 未采集`；系统托盘可见项 `InputTracker - 未采集 → InputTracker - 已暂停 → InputTracker - 未采集`，命令退出码 0。
- 退出：通过真实“退出”按钮结束，5 秒内 `REMAINING_PROCESSES=0`；首次退出探针仅为 PowerShell 括号语法错误，纠正后通过，未改产品。
- 截图限制：受控桌面全屏捕获为黑屏，WPF `PrintWindow` 仅标题栏/空白客户区；两张 ignored 诊断图不能作为正向视觉证据。
- 结果：自动、CI、单实例和真实状态控制链通过；普通用户可视启动/UAC 与可读截图仍为 B-005，不声称最终/现场验收完成。
- 下一步：提交并推送本记录；由 C1 重新判断 B-005 是否阻塞 PR 集成，并行继续不依赖现场截图的只读后续准备，禁止直接推 `main`。

## 2026-08-05 PR #1 独立 merge gate

- 分支/提交：`codex/bootstrap@3827b37`；工作树在检查前干净，远端 head 一致。
- GitHub：PR #1 为 `MERGEABLE/CLEAN`；最新 head 两次 `CI / windows` 均 `SUCCESS`；无 review、review request 或未解决 review thread。
- C1 结论：选择 B，可转 Ready 并经 PR 合并；B-005 是最终/现场验收阻塞，不是已知代码、CI、状态链或合并完整性故障。
- 理由：非管理员中文空格目录自检、`asInvoker/uiAccess=false`、真实窗口/托盘暂停恢复和无残留进程已形成互补证据；继续阻塞只会卡住 Input Core，不能增加当前环境可验证性。
- 约束：合并不等于产品最终验收；B-005 必须继续保留。转 Ready 或更新 head 后重新核对 head SHA、`CLEAN` 与 checks，并且只能经 PR 合并。
- 下一步：修正 B-005 与线程索引中的 draft 表述，提交并推送；等待新 head CI 全绿后转 Ready 并合并 PR #1。

## 2026-08-05 Bootstrap PR 合并与 Input Core 启动

- Bootstrap 最终 head：`codex/bootstrap@3bb0e97`；更新后的两项 `CI / windows` 均为 `SUCCESS`，GitHub 报告 `MERGEABLE/CLEAN`。
- PR：`https://github.com/LinkStartR/InputTracker/pull/1` 已从 draft 转 Ready，并按 expected head 经 PR 合并；结果 `merged=true`，merge commit=`1fc617728ab22f03ac9c0339cddc8501886669f1`。
- 命令：`git fetch origin main`、`git switch main`、`git merge --ff-only origin/main`、`git switch -c codex/input-core` 均退出码 0。
- 分支：`codex/input-core` 从最新 `main@1fc6177` 创建；`git status --short --branch` 输出 `## codex/input-core`，工作树干净。
- C4 只读评审：建议独立 `InputTracker.Input` 类库、单一 Win32 消息线程、单写单读有界 `Channel<T>`、后台工作线程；回调仅做 Raw Input 读取、最小规范化和 `TryWrite`。
- 数据正确性：有界且非阻塞的入口无法对任意过载数学保证永不丢；支持负载内须证明零丢失，`TryWrite` 失败必须立即进入 `IntegrityFault` 并停采，禁止静默丢弃或继续伪报完整。
- 设备边界：`hDevice` 仅为本次进程句柄；接口路径和硬件信息只作为可解释身份线索，长期重插映射及置信提示由 Storage Stats 承接，不伪称无序列号设备永久唯一。
- 隐私边界：键盘公共事件仅含运行时设备 ID、物理扫描码、E0/E1、按下/抬起及聚合所需时间戳；禁止字符、键名、窗口、进程、焦点和持久化逐键顺序。
- 阻塞保留：B-003、B-005 继续阻塞最终现场验收，但不阻塞 Input Core 自动化实现；本阶段不新增依赖、权限、驱动或服务。
- 下一步：在隔离 worker 分支实现 Raw Input、设备生命周期、有界管线和百万事件零丢失/零乱序普通测试；主任务审查后集成到 `codex/input-core`，再交 C2 增加阶段判卷。

## 2026-08-05 C2 Input Core 判卷只读设计

- 分支/提交：主工作区 `codex/input-core@0a31520`；C2 本轮只读，未建分支、改文件、提交或推送。
- 基线：Bootstrap 的 `total>=11`、`acceptance>=5`、`gates>=11` 必须原样回归；Input Core 仅追加测试、gate 和基线下限，不得删减。
- 自动判定：生产队列/worker 的百万事件精确计数、overflow/sink 异常 `IntegrityFault`、暂停原因叠加与 barrier、键盘公共 schema、鼠标全量字段、设备代次隔离与插拔生命周期。
- 证据契约：C4 需暴露只读 diagnostics/state snapshot 与可替换 sink/registration seam，使判卷能区分“真正关入口并注销”与“只改 UI 文案”。
- 隐私例外边界：`RAWKEYBOARD` 的 `VKey/Message` 仅可存在于唯一 private interop 布局文件并列入精确 allowlist；normalizer、公共 DTO 和产品调用不得传出或使用。
- 硬件边界：自动测试注入已规范化事件，不是 Raw Input 物理验收；InputCore 阶段证据必须明确 `hardwareTerminalStatus=not-run`、`releaseEligible=false`，B-003 不解除。
- 判卷规划：C4 API 稳定后由 C2 独占新增 5 组 Acceptance、硬件 runbook、性能 evidence schema、动态测试项目发现及 `-Stage InputCore`；不预建 skip/todo 测试。
- 反向验证规划：仅在 Acceptance 注入器使用一次 out-of-order 变异，判卷必须因 `outOfOrder>0` 退出 1；移除注入后重跑全绿，生产代码和工作树不留变异。
- 下一步：C4 完成稳定 API 和普通测试；主任务审查集成后再授权 C2 落地判卷，先回归 Bootstrap 后执行 InputCore 红→绿。

## 2026-08-05 C1 Input Core 核心提交边界复核

- 分支/提交：C4 worker `30814c0 feat: add raw input capture core`，父提交 `0a31520`；C1 本轮只读，未改文件或执行 Git 写操作。
- 精确范围：20 个文件、2529 行新增、0 删除；仅 `InputTracker.sln`、`PROGRESS.md`、`src/InputTracker.Input/**`、`tests/InputTracker.Input.Tests/**`。
- 排除项：`tests/Acceptance/**`、`scripts/verify.ps1`、`LICENSE`、`.github/**`、`.tools/**`、`bin/obj` 均未进入提交；未提交的 8 个 App 接入文件亦未混入。
- 依赖/权限：核心项目无 `PackageReference`；测试复用既有锁版三包；仅使用 Raw Input 所需 `user32.dll`/`kernel32.dll`，未发现注册表、服务、网络、文件写入、Startup 或提权配置。
- 测试单调性：父提交共有 11 个测试属性，提交后为 27 个，新增 16 个且既有测试未删除；`git diff --check` 通过。
- 结论：核心提交边界适合主任务 cherry-pick；`PROGRESS.md` 因主分支新增 C2 记录可能需由主任务解决上下文冲突，集成后必须重跑 restore/build/test。
- 下一步：等待 C4 独立提交 App 接入及完整五命令结果；主任务做代码正确性审查后按顺序集成，不把核心提交单独视为产品接入完成。

## 2026-08-05 Input Core 采集核心与生产管线

- 分支/提交：`codex/input-core-work@0a31520`；本项待提交，未 push。
- 范围：新增独立 `InputTracker.Input` 类库和普通测试项目；加入隐藏 Win32 消息窗口、keyboard/mouse 的 `RIDEV_INPUTSINK | RIDEV_DEVNOTIFY` 注册、`WM_INPUT`/插拔/私有控制消息、单写单读有界 Channel、后台 worker、设备代次、暂停原因与只读诊断；未修改 `tests/Acceptance/**` 或 `scripts/verify.ps1`。
- 隐私：公共键盘 DTO 仅含运行时设备 ID、MakeCode、E0/E1、Down/Up 和 timestamp；入口序号仅为内部管线诊断；源码测试禁止文字、键名、窗口、进程和前台 API。
- restore：沙箱首次因 `AppData/Local/Microsoft SDKs` 读取被拒退出 1；按权限流程受控重试后 5 项目恢复成功，退出码 0。
- build：首次核心构建 0 errors、2 个可空性 warnings；改为值类型设备条目后复核为 0 warnings / 0 errors，退出码 0。
- test 第 1 次：65,536 容量的瞬时百万生产循环按预期触发 `TryWrite=false`，1 失败/4 通过；未放宽 overflow 断言，改用同一生产队列的明确 1,000,000 有界容量，另保留容量 1 的溢出故障测试。
- test 第 2 次：百万混合流因最初两个键盘事件为无对应 Down 的 Up，sink 实际收到 `999,998`，1 失败/18 通过；修正生成序列为合法 Down/Up，未修改生产过滤逻辑或目标计数。
- test 最终：`InputTracker.Input.Tests` 共 20 passed、0 failed、0 skipped，含真实 User32 注册/暂停/恢复/注销测试；百万事件 `produced=accepted=dequeued=sinkAcknowledged=1,000,000`，`overflow=parseErrors=outOfOrder=0`，测试 sink 仅保留计数与滚动 hash。
- 未验证：真实两键盘两鼠标、前后台 2 小时及 1000Hz 鼠标 30 分钟仍为 B-003；自动测试和真实 User32 注册不冒充物理硬件终验。
- 结果：核心契约、Win32 入口、有界管线、设备隔离、暂停 barrier、overflow/sink failure 的 IntegrityFault 和公开诊断已完成并自动验证。
- 下一步：提交本核心单元；随后提交 App 最小生命周期接入，再运行统一五命令和 Bootstrap 回归。

## 2026-08-05 Input Core App 生命周期接入

- 分支/提交：核心单元已提交为 `30814c0 feat: add raw input capture core`；本 App 接入项待提交，未 push。
- 接入：App 在程序目录可写时创建真实 `InputCaptureService`，窗口显示后异步启动；退出时按 Input Core 的注销、barrier、worker、隐藏窗口顺序释放。程序目录拒绝、自检和 hold-smoke 不启动采集。
- 状态：窗口和托盘诚实区分“正在启动、采集中、已暂停、完整性故障、启动失败”；明确 Input Core 阶段事件尚未持久化，不把实时汇总冒充 Storage。
- 控制：窗口与托盘通过 `PauseReason.User` 异步暂停/恢复；`SessionLocked` 或 `SystemSuspend` 仍存在时，用户恢复不会重新注册采集。
- sink：App 当前 sink 只做线程安全计数，不保存事件对象、键盘顺序、文字、窗口或进程；后续由 Storage Stats 替换持久化实现。
- build：`dotnet build InputTracker.sln -c Release --no-restore` 退出码 0，5 项目成功，0 warnings / 0 errors。
- test：`InputTracker.App.Tests` 共 7 passed、0 failed、0 skipped；新增状态映射、用户暂停原因和 IntegrityFault 可见性测试。
- 结果：Input Core 已最小接入真实 App 生命周期，Bootstrap 原有目录 fail-closed、单实例和自检路径保持不采集。
- 下一步：提交 App 接入单元；随后运行完整 solution 测试、Bootstrap 判卷回归和 self-contained publish。

## 2026-08-05 Input Core 故障时序与退出清理收口

- 分支/提交：`codex/input-core-work`，已有 `30814c0`、`1fa152a`；本修复待独立提交，未 amend、未 push。
- worker 故障：sink 异常后先关闭入口，通过带 completion 的消息线程控制请求完成 Raw Input 注销，再发布 `IntegrityFault`；真实/假 host 测试均在状态事件时断言 `IngressOpen=false`、`RawInputRegistered=false` 且注销计数已增加。
- barrier 竞态：worker 离开消费态时封闭队列并故障所有待处理 barrier；Stop 在改为 Stopping 前保留原状态，并结合 worker 消费态决定是否请求新 barrier。并发 Stop 与未完成 worker fault callback 的测试在 5 秒边界内完成，不遗留无人消费的 barrier。
- 退出清理：`PrepareStopAsync` 失败后仍依次尝试 complete queue、等待 worker、销毁 host；保留并重新抛出首个异常，同时发布可观察的 `IntegrityFault`。fake-host 注销失败测试确认入口关闭、销毁已尝试且调用方收到原异常。
- App 异常：启动 catch 不再为空；窗口和托盘的 async 控制异常统一转为可测试的可见状态。仅 diagnostics 同时证明入口和 Raw Input 注册关闭时显示“已停止”，否则使用 `ControlUncertain` 明确“状态不确定、采集可能仍在运行、请退出程序”。
- 受影响测试第 1 次：Input 测试 22 passed / 1 failed；失败源于原生 host 启动后的设备到达可能先触发始终失败的 sink，未据此放宽生产断言。
- 受影响测试第 2 次：Input 测试 23 passed / 0 failed / 0 skipped；随后按审查意见将 native sink 改为仅在真实 pause `AggregationBoundary` 失败，消除 arrival 消费竞态。
- 受影响 solution 复核：加入诊断分支测试前 Input 23、App 9、Acceptance 5，合计 37 passed / 0 failed / 0 skipped；Acceptance 与 verify 文件未修改。
- 提交前受影响项目复核：确定性 boundary sink 的 Input 测试 23 passed / 0 failed / 0 skipped；新增 `ControlUncertain` 诊断分支后的 App 测试 10 passed / 0 failed / 0 skipped。
- 高频验收边界：当前热路径仅逐个 `WM_INPUT` 调用 `GetRawInputData`，没有 `GetRawInputBuffer` buffered drain；本阶段不声称已验证 1000Hz，是否加入 buffered drain 由 B-003 的真实 1000Hz/30 分钟终验决定。
- 下一步：提交本聚焦修复；从该提交重新执行统一五命令并记录最终输出。

## 2026-08-05 Input Core 主分支集成复核

- 分支/提交：`codex/input-core` 按序集成为 `7f5fda8`（核心）、`9c633d0`（App 接入）、`cb4c2da`（故障收口）；未 push。
- C1 只读复核：范围仅含 Input Core、最小 App 接入、普通测试和进度/阻塞文档；Acceptance、verify、LICENSE、依赖、权限、注册表、服务、网络及隐私禁止项均未越界；测试属性 `11→27→28→34`。
- 首次主工作区 restore：沙箱拒绝读取 `C:\Users\Administrator\AppData\Local\Microsoft SDKs`，5 项目报 `MSB4184`，退出码 1；未改代码或系统配置。
- 受控重试：restore 5 项目退出码 0；Release build 5 项目成功，0 warnings / 0 errors；solution 测试 Input 23、App 10、Acceptance 5，共 38 passed / 0 failed / 0 skipped，退出码 0。
- worker 验证证据：Bootstrap verify `.tools/verification/20260805-161811-975/verification.json` 为 passed；其脚本当前只统计 App+Acceptance 15 项，Input 测试由上述 solution 命令实际执行，C2 的 InputCore stage 将修复动态发现缺口。
- 结果：三个已审查实现提交已进入主任务分支，硬件终验 B-003 仍未解除，不把自动化或 User32 注册测试冒充真实两键盘两鼠标/1000Hz 结论。
- 下一步：集成 C4 最终五命令记录后，授权 C2 基于稳定 API 实现 `-Stage InputCore`、Acceptance 与红→绿反向验证。

## 2026-08-05 Input Core 统一五命令终验

- 分支/提交：`codex/input-core-work@a5aa404`；聚焦修复为独立提交 `fix: synchronize input fault shutdown`，未 amend `30814c0`/`1fa152a`，未 push。
- `./.tools/dotnet/dotnet.exe restore InputTracker.sln`：退出码 0，5 个项目均为最新，无新增 NuGet。
- `./.tools/dotnet/dotnet.exe build InputTracker.sln -c Release`：退出码 0，Input、App、两组普通测试与 Acceptance 全部生成成功，0 warnings / 0 errors。
- `./.tools/dotnet/dotnet.exe test InputTracker.sln -c Release --no-build`：退出码 0；Input 23、App 10、Acceptance 5，合计 38 passed / 0 failed / 0 skipped。
- 百万事件：本轮普通测试执行生产 `BoundedInputQueue` + worker 的 4 设备混合 1,000,000 事件断言，`produced=accepted=dequeued=sinkAcknowledged=1,000,000`，`overflow=parseErrors=outOfOrder=0`；sink 仅做计数与滚动 hash，不保存百万对象，不冒充硬件验收。
- `powershell -ExecutionPolicy Bypass -File scripts/verify.ps1 -Stage Bootstrap`：退出码 0；SDK `10.0.302`，build 0 warnings / 0 errors，既有判卷范围 `total=15`（App 10 + Acceptance 5）、`acceptance=5`、`failed=0`、`skipped=0`、`gates=11`；Input 23 项由上一条全 solution 测试覆盖。
- 判卷证据：ignored `.tools/verification/20260805-161811-975/verification.json`，状态 `VERIFY PASSED`；未提交运行产物或 `.tools/dotnet` junction。
- `./.tools/dotnet/dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true`：退出码 0；App 与 Input 发布到 ignored `.tools/build/**/win-x64/publish/`。
- 边界检查：未修改 `tests/Acceptance/**`、`scripts/verify.ps1`、`LICENSE`、`.github`；无新依赖、管理员权限、驱动、服务、遥测或运行时网络。
- 未验证：B-003 的真实两键盘两鼠标前后台 2 小时与 1000Hz 鼠标 30 分钟仍未执行；当前仅逐 `WM_INPUT` 使用 `GetRawInputData`，是否需要 `GetRawInputBuffer` buffered drain 等待真实终验数据决定。
- 结果：Input Core 自动化实现、故障收口、App 最小接入和 Bootstrap 回归均通过；代码可交主任务集成，但最终硬件验收仍保持阻塞。
- 下一步：提交本纯验证记录并回报主任务；不 push、不创建 PR。

## 2026-08-05 Storage 只读设计与 Input Core 兼容门

- 分支/提交：主任务 `codex/input-core@99690ce`；C5 本轮只读审查，未建分支、未修改、未提交或推送。
- 设备描述门：当前 `RawDeviceRegistry.Resolve()` 可在首个输入时隐式创建设备，但 `InputWorker` 不向 sink 发送对应 `DeviceChange.Arrived`；Storage 因而只能看到进程内 `RuntimeDeviceId`，拿不到接口路径、硬件摘要与置信度，无法正确实现重插映射。
- 时间边界：事件 `Timestamp` 属于 `Stopwatch` 单调时钟域，不能当作 UTC/Unix ticks；Storage 与 Input Core 同进程时可用同一频率建立 UTC 校准段，因此不是立即阻塞，后续建议非破坏性暴露时钟元数据。
- 持久化风险：当前 sink 为逐事件 await；Storage 可以在暂停、移除、停止 boundary 强制落盘，但若把 `SinkAcknowledged` 定义为抵抗突然断电的逐事件持久确认，则需要批量 durable-ack 契约，进入 Storage 技术尖峰前不得静默假设。
- 处置：已向可定位的 C4 线程下发最小兼容修补，只允许保证 descriptor-before-first-event 并增加普通测试；不得碰 Acceptance、判卷脚本、依赖或 Storage。
- 下一步：C2 完成 InputCore 阶段判卷；C4 返回独立兼容提交后由主任务审查、集成并重跑统一命令。

## 2026-08-05 Input Core Acceptance 判卷授权

- 分支/基线：`codex/input-core-acceptance@99690ce`；C2 仅维护 `tests/Acceptance/**`、`scripts/verify.ps1`、验收计划与本记录。
- 例外：为让 Acceptance 直接执行真实内部 `BoundedInputQueue`、`InputWorker` 与 host seam，按主任务明确授权在 `src/InputTracker.Input/Properties/AssemblyInfo.cs` 精确追加 `InternalsVisibleTo("InputTracker.AcceptanceTests")`，并让 Acceptance 项目引用 Input；未公开运行时注入 API、未添加环境变量旁路、未改生产逻辑。
- 目标：追加无 skip 的百万计数、IntegrityFault、pause barrier、隐私 allowlist、鼠标/设备代次、硬件 runbook/schema 与 `verify.ps1 -Stage InputCore`；Bootstrap gate 和基线单调性必须回归。

## 2026-08-05 Input Core Acceptance 判卷结果

- 范围：新增 7 个无 skip 的 InputCore Acceptance tests；基线升至 `minimumTotalTests=45`、`minimumAcceptanceTests=12`、`minimumGateCount=23`，所有既有 Bootstrap gate 均保留。
- 唯一产品侧可见性例外：`src/InputTracker.Input/Properties/AssemblyInfo.cs` 精确追加 `InternalsVisibleTo("InputTracker.AcceptanceTests")`，让验收项目能直接驱动真实内部 queue/worker/host seam；未公开运行时注入 API、未添加环境变量生产旁路、未修改生产逻辑。
- 自动判卷：`verify.ps1` 动态发现 3 个 `IsTestProject=true` 项目；InputCore 检验百万事件 `produced=accepted=dequeued=sinkAcknowledged=1,000,000`，以及 overflow/sink IntegrityFault、pause barrier、精确 interop allowlist、鼠标全字段和设备代次。InputCore 自动证据明确写入 `hardwareTerminalStatus=not-run`、`releaseEligible=false`。
- 性能 schema：强制 SDK/runtime、logical processor count、每台设备的 `deviceKind`/interfaceId/hardwareSummary/identityConfidence、至少 2 Keyboard 与 2 Mouse、1000Hz 依据、前后台场景、30 分钟时长、CPU/working set/UI latency 阈值和 Storage 前不可伪填为 0 的 diskGrowth 状态。
- 反向验证：`verify.ps1 -Stage InputCore -InjectInputCoreOutOfOrder` 按预期退出 1，证据观察到 `outOfOrder=1`；移除注入后 `verify.ps1 -Stage InputCore` 退出 0，`total=45`、`acceptance=12`、`failed=0`、`skipped=0`、`gates=23`，证据位于 ignored `.tools/verification/20260805-170122-714/verification.json`。
- Bootstrap 回归：`verify.ps1 -Stage Bootstrap` 退出 0，`total=45`、`acceptance=12`、`failed=0`、`skipped=0`、`gates=11`；程序目录外 `-InjectedDataPath C:\InputTracker-outside` 按预期退出 1。
- 统一命令：本地 `.tools/dotnet` SDK `10.0.302` 的 restore、Release build（0 warnings / 0 errors）、solution test（Input 23 + App 10 + Acceptance 12，合计 45 passed / 0 failed / 0 skipped）和 self-contained win-x64 publish 均退出 0。
- 未验证：真实两键盘两鼠标、前后台 2 小时、1000Hz 鼠标连续 30 分钟仍是硬件终验；自动化和真实 User32 seam 测试不冒充该结论。

## 2026-08-05 Input Core 隐式设备到达兼容修补

- 分支/提交：`codex/input-core-work@add8d40`；本项待独立提交，未 push。
- 契约：保留既有 `RawDeviceRegistry.Resolve(...) -> InputDeviceDescriptor` 与 `Arrive(...)` 签名，新增带 `out bool created` overload；独立 Acceptance 可继续按旧签名编译，worker 使用新契约判断是否发送到达事件。
- 顺序：非零未知 hDevice 的首个 Keyboard/Mouse 输入前，worker 先以同一 timestamp 向 sink 写入并确认包含接口路径、硬件摘要与置信度的 `DeviceChange.Arrived`；确认完成后才写首输入。
- 幂等：显式到达后输入不重复 Arrived；隐式到达后迟到的显式到达同样不换 runtime id、不增加 generation；Removal 后同句柄再出现仍创建新 generation/runtime id。
- 零句柄：隐式输入保持 `Unattributed`，显式到达直接忽略，均不查询硬件或伪造设备描述。
- 测试覆盖：未知 Keyboard/Mouse 首输入 ack 顺序、显式到达无重复、隐式后迟到显式到达、零句柄、Removal 后隐式再现。
- Input 测试第 1 次：编译退出码 1，公开 Theory 暴露 internal `IngressEventKind` 触发 `CS0051`；改为公开 `int` 参数并在测试内转换，未扩大产品 API。
- Input 测试第 2 次：29 passed / 0 failed，出现 2 条 xUnit2031；按分析器建议改为 predicate overload，未放宽断言。
- 最终 Input 测试：退出码 0，29 passed / 0 failed / 0 skipped，无警告。
- 最终 solution 测试：退出码 0；Input 29、App 10、Acceptance 5，合计 44 passed / 0 failed / 0 skipped。
- 边界：仅修改 Input registry、worker、普通 Input 测试和本进度记录；未修改 App/Storage、`tests/Acceptance/**`、`scripts/verify.ps1`、依赖、权限、隐私字段或阈值。
- 下一步：提交独立兼容修补并回报主任务，不 push。

## 2026-08-05 Input Core 组合集成与红→绿复核

- 分支/提交：主任务 `codex/input-core@d3551d3`；C2 `25d3e11` 集成为 `03985a4`，C4 `b4efeb4` 集成为 `d3551d3`。两次 cherry-pick 均仅在 `PROGRESS.md` 冲突，主任务保留双方完整记录后继续；功能、测试与判卷文件无冲突。
- `./.tools/dotnet/dotnet.exe restore InputTracker.sln`：退出码 0；5 项目恢复完成或已是最新。
- `./.tools/dotnet/dotnet.exe build InputTracker.sln -c Release --no-restore`：退出码 0；5 项目成功，0 warnings / 0 errors。
- `./.tools/dotnet/dotnet.exe test InputTracker.sln -c Release --no-build --no-restore`：退出码 0；Input 29、App 10、Acceptance 12，共 51 passed / 0 failed / 0 skipped。
- 组合判卷：`scripts/verify.ps1 -Stage InputCore` 退出码 0；`total=51`、`acceptance=12`、`failed=0`、`skipped=0`、`gates=23`，最终证据 `.tools/verification/20260805-172008-554/verification.json`。
- 乱序红→绿：`-Stage InputCore -InjectInputCoreOutOfOrder` 在 `.tools/verification/20260805-171748-522` 观察到乱序并明确退出 1；移除注入后 `.tools/verification/20260805-171829-815/verification.json` 为 51/12、23 gates 全绿，退出 0。
- 绿色路径红→绿：`-Stage Bootstrap -InjectedDataPath C:\InputTracker-outside` 明确输出“越出程序目录”并退出 1；未修改工作树后用正式 InputCore 判卷复跑为 `.tools/verification/20260805-172008-554/verification.json`，退出 0。
- self-contained publish 第 1 次：额外使用任务书未要求的 `--no-restore`，Input 项目缺少 win-x64 assets target，报 `NETSDK1047` 并退出 1；未改代码或阈值。
- self-contained publish 第 2 次：按任务原参数 `./.tools/dotnet/dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true` 重跑，完成 5 项目 RID restore，App 与 Input 生成 win-x64 publish 目录，退出 0。
- 未验证：B-003 两键盘两鼠标前后台连续 2 小时、1000Hz 鼠标 30 分钟与 B-005 普通用户/UAC 现场证据仍未执行；当前自动证据继续明确 `hardwareTerminalStatus=not-run`、`releaseEligible=false`。
- 下一步：由固定 C1 线程只读复核 `main@1fc6177..d3551d3`，通过后提交本记录、push `codex/input-core` 并创建 draft PR；禁止直接推 main。

## 2026-08-05 C1 Input Core 集成分支只读审查

- 审查范围：`main@1fc6177..d3551d3`，并复核审查期间新增且仅含进度文档的 `511e414`；C1 未修改文件、未执行 Git 写操作或 GitHub 操作。
- 输出：整体 42 文件、4579 行新增、55 行删除；`git diff --check` 退出码 0，工作树干净，`origin/main...HEAD` 为 `0 13`，远端尚无 `codex/input-core`。
- 边界：`LICENSE` 未变；`.tools` 运行产物与 `bin/obj` 未跟踪；无新增 NuGet 包；未发现注册表、服务、网络、文字、窗口标题、前台窗口或进程采集 API；Raw Input 仅使用必要的 user32/kernel32。
- 测试单调性：测试属性从 `main@1fc6177` 的 11 增至 `d3551d3` 的 46；`03985a4` 的 C2 独占文件归属正确，`d3551d3` 的 descriptor-before-first-event 有普通测试与 Acceptance 覆盖。
- 冲突说明：当前 `git ls-files -u` 为空，`PROGRESS.md` 同时保留 Input Core、Storage 与 Acceptance 记录；因历史为线性 cherry-pick、无 merge commit，不能仅凭最终历史严格证明过去所有冲突都只发生在 `PROGRESS.md`，但当前未发现代码冲突残留。
- 结论：允许推送当前完整 HEAD；建议 PR 标题 `Input Core: add Raw Input capture and acceptance gates`，正文必须明确真实多设备、1000Hz 与现场证据仍未通过。
- 下一步：提交本审查记录，执行 `git push -u origin codex/input-core`，创建 draft PR，等待 CI 与 PR 状态复核。

## 2026-08-05 Input Core 推送与 draft PR

- 分支/提交：`codex/input-core@ea4496d`；`git push -u origin codex/input-core` 退出码 0，首次创建远端同名分支并设置 upstream；未直接推送 `main`。
- PR：GitHub 创建 draft PR #2 `Input Core: add Raw Input capture and acceptance gates`，base=`main@1fc6177`，head=`codex/input-core@ea4496d`，URL `https://github.com/LinkStartR/InputTracker/pull/2`。
- PR 输出：14 commits、42 changed files、4603 additions、55 deletions；创建时 GitHub 返回 `state=open`、`draft=true`、`mergeable=false`，该字段可能仍在异步计算，不能据此宣称可合并。
- PR 声明：正文列出 51/12/23/0 的自动验证与两类红→绿，同时明确两键盘两鼠标 2 小时、1000Hz 30 分钟和普通用户/UAC 现场证据未完成，`releaseEligible=false`。
- 下一步：提交并推送本 PR 记录；等待 GitHub CI 和 mergeability 稳定，检查审查线程后再决定是否可从 draft 转为 ready。Storage 分支不得在 Input Core PR 合并前开始实现。

## 2026-08-05 Input Core PR #2 CI 首轮失败与修复

- 检查：`gh pr checks 2 --repo LinkStartR/InputTracker` 返回两个 `windows` 均为 fail，分别为 pull_request run `30993493390` 与 push run `30993490199`，用时 1m3s / 1m6s。
- 工具过程：按 `github:gh-fix-ci` 先执行 `gh auth status`，账号已认证且具有 `repo`/`workflow` scope；其 bundled Python 检查脚本因本机 GBK 解码 GitHub UTF-8 日志触发 `UnicodeDecodeError` 后又因空日志触发 `AttributeError`，未据此作产品结论，改走 skill 规定的 `gh` 手工回退。
- 日志：两次 run 均在 `Publish portable win-x64` 失败；命令含 `--no-build`，`InputTracker.Input` 的 assets 文件没有 `net10.0-windows10.0.19041.0/win-x64` target，报 `NETSDK1047` 并退出 1；Restore、Build、Test 阶段未被日志标为失败。
- 根因：CI 的非 RID restore/build 不能满足随后 solution 级 win-x64 publish 的 `--no-build` 前置条件；与组合验证中额外 `--no-restore` 的首次失败属于同一 RID assets 边界，不是产品采集逻辑或测试失败。
- 修复：仅删除 `.github/workflows/ci.yml` Publish 步骤的 `--no-build`，使 CI 与任务书原命令一致并允许 publish 自行执行必要 RID restore/build；未改产品代码、依赖、测试、断言或阈值。
- 下一步：运行本地原 publish 命令与 diff 检查，提交推送后等待新的 push/PR 两次 CI；旧失败 run 不重跑，因为新提交会自然触发新检查。

## 2026-08-05 Input Core PR #2 CI 修复结果

- 分支/提交：CI 修复与记录提交为 `acf2fc2 ci: restore RID assets before publish`，push 退出码 0，远端从 `b2ee5ea` 前进到 `acf2fc2`。
- 本地复核：`./.tools/dotnet/dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true` 退出码 0；Input 与 App 均生成 win-x64 publish 目录，`git diff --check` 退出码 0。
- C1 只读复核：确认 workflow 唯一功能变更为删除 publish 的 `--no-build`；`tests/**`、`scripts/verify.ps1`、产品代码、依赖、权限、隐私和阈值均无变化，允许提交推送。
- 新 CI：push run `30993971924` 的 `windows` 为 pass，用时 1m08s；pull_request run `30993976154` 的 `windows` 为 pass，用时 1m22s。
- PR 状态：`gh pr view 2` 返回 head=`acf2fc2`、base=`1fc6177`、`isDraft=true`、`mergeable=MERGEABLE`、`mergeStateStatus=CLEAN`，两个 status check 均 `SUCCESS`，无 review decision。
- 下一步：提交并推送本结果记录，等待该文档提交触发的最后一轮 CI；若仍全绿且无 review thread，将 PR #2 转为 ready 并按主任务合并顺序合并到 main。

## 2026-08-05 Input Core PR #2 合并与 Storage 分支建立

- 最后 CI：结果记录提交 `281e4ae` 触发 push run `30994198402` 与 pull_request run `30994201711`，`windows` 均 pass，用时 1m05s / 1m13s。
- 合并前状态：PR #2 head=`281e4ae`、base=`main@1fc6177`、`MERGEABLE/CLEAN`，两个 status checks 均 `SUCCESS`，0 reviews、0 review threads。
- GitHub：PR #2 从 draft 转为 ready 后，以 expected head `281e4ae` 和 merge method `merge` 成功合并；GitHub 返回 merge SHA `f6136c31ff35461adadf3766f5e8e25e88a638e7`。
- 本地同步：`git fetch origin` 取得 `main 1fc6177..f6136c3`；`git switch main` 后 `git merge --ff-only origin/main` 成功快进 18 commits，未直接推 main。
- 新分支：确认无现有本地 `codex/storage-stats` 后，从最新 `main@f6136c3` 执行 `git switch -c codex/storage-stats`，退出码 0，工作树干净。
- 结论：Input Core 自动化阶段完成并经已审查 PR 进入 main；B-003 真实多设备/1000Hz 与 B-005 普通用户现场证据仍保留，不因合并解除。
- 下一步：先执行 Storage durable-ack/SQLite/轨迹块技术尖峰并固定契约，再实现任务 3；C2 仅在稳定 API 后追加 Storage 阶段 Acceptance，禁止预建 skip/todo。

## 2026-08-05 C2 Storage Stats 验收只读设计

- 分支/状态：主任务 `codex/storage-stats@6f31d84`；C2 本轮只读，未建分支、未改文件、未提交/push，也未运行功能或硬件验收。
- durable 语义：`SinkAcknowledged` 仅表示 Storage sink 接收，不等于 fsync；`durableWatermark` 必须由 C5 定义为块与 manifest/index 协议已持久化的最高连续序号，最终 flush 后百万事件才要求达到终点。
- 崩溃边界：进程 kill 只证明恢复完整 durable prefix、无 gap/dup；非 durable tail 必须精确报告且不得伪称保证。突然断电证据必须标记 physical / storage-layer simulation / not-run，无物理证据不得令 `releaseEligible=true`。
- Gate 规划：Storage 阶段保留 Bootstrap+InputCore 全部 gate，并追加百万 final flush、durable watermark、进程恢复、块完整性、迁移、四周期/DST/时区、隐私、设备置信度、CSV/PNG、备份恢复、范围删除、磁盘增长和两类注入边界等 19 个 gate；baseline 仍只增不减。
- 时间验收：半开区间 `[start,end)`、ISO 周一开周；日/周/月/年覆盖 Pacific 23h 春跳/25h 秋回的两个本地 01:30，以及 China Standard Time 与 UTC，期望与观测不得重漏。
- 红→绿：仅在 Acceptance 临时副本翻转已提交轨迹块一位，或修改 Acceptance expected fixture；verify 必须 exit 1，恢复后全绿。禁止改生产工作树、阈值或提交 mutation。
- API 前置：C5 稳定提供 writer/flush/watermark/reopen/recovery、fixed clock/timezone seam、仅测试可见 fault points、manifest/block/SQLite 迁移及导出/备份/删除的可观察行为，并定案 B-006 后，C2 才落无 skip 的 Storage Acceptance 与 `verify -Stage Storage`。
- 下一步：等待 C5 durability 尖峰结果；API 未稳定前不创建空测试、skip 或 todo。

# 2026-08-05 Storage durability 第一小块首次 restore 阻塞

- 分支/提交：`codex/storage-stats-work@f6136c3` 加未提交第一小块；未 push。
- 范围：仅锁定 `Microsoft.Data.Sqlite 10.0.10`，新增 Storage/普通测试项目、程序目录 layout、SQLite WAL/FULL/foreign keys/secure delete/busy timeout、v1 migration、校准段和隐私 schema tests；未触碰 Acceptance、verify、App/Input/UI/portable。
- 首次 restore：仓库本地 `.tools/dotnet` 因沙箱拒绝读取 `C:\Users\Administrator\AppData\Local\Microsoft SDKs` 报 `MSB4184`，退出码 1；与既有环境门一致，不是编译或 NuGet 结论。
- 受控重试：按权限流程请求相同 restore 的沙箱外执行，但 Codex 当前用量额度阻止审批，命令未执行；未改 TFM、SDK 探测或 NuGet 配置绕过。
- 当前状态：尚无有效 restore/build 结果，依赖图和 `select sqlite_version()` 尚未产生；按任务卡暂停 spool/recovery 实现，等待同一命令获得受控执行能力。

## 2026-08-05 Storage durability 第一小块有效构建与首轮测试红

- 主任务代跑 restore：`./.tools/dotnet/dotnet.exe restore InputTracker.sln` 退出码 0；7 项目恢复，Storage 与 Storage.Tests 的 `Microsoft.Data.Sqlite 10.0.10` 均成功。
- 主任务代跑 build：`./.tools/dotnet/dotnet.exe build InputTracker.sln -c Release --no-restore` 退出码 0；7 项目生成，0 warnings / 0 errors，用时 5.51 秒。
- 主任务代跑 Storage tests 首轮：退出码 1；5 total，3 passed / 2 failed / 0 skipped。
- 失败：`FutureSchemaVersionFailsClosed`、`MigrationChecksumMismatchFailsClosed` 的 fail-closed 断言完成后，临时目录清理因测试手工 SQLite 连接默认 `Pooling=true` 仍持有数据库文件而报 `IOException`；产品连接已为 `Pooling=false`。
- 修复：仅把两处测试准备连接改为 `Pooling=False`，不改 future/checksum 断言、产品 schema 或迁移行为；待重编译重跑后记录绿色结果。

## 2026-08-05 Storage migration 与 spool 红→绿

- migration 测试修复后，主任务代跑 Storage.Tests build 退出码 0，0 warnings / 0 errors，用时 1.15 秒；Storage tests 5 passed / 0 failed / 0 skipped，用时 295ms。
- 依赖图：`Microsoft.Data.Sqlite` / `Microsoft.Data.Sqlite.Core` 均为 `10.0.10`；`SQLitePCLRaw.bundle_e_sqlite3`、`core`、`lib.e_sqlite3`、`provider.e_sqlite3` 均为 `2.1.11`。
- spool/recovery 首次真实编译退出码 1，0 warnings / 2 errors：`MouseSpool.cs(151)` 的实例属性 `string Path` 遮蔽 `System.IO.Path`，导致 `GetFullPath` 与 `GetDirectoryName` 无法解析。
- 修复仅在该行显式限定 `System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))`，未改 spool 格式、CRC、恢复协议或断言。
- 主任务代跑相同 Storage.Tests build 退出码 0，0 warnings / 0 errors，用时 1.64 秒；Storage tests 10 passed / 0 failed / 0 skipped，用时 244ms。
- 当前覆盖：CRC32C 截断尾精确恢复、CRC 损坏 fail-closed、sink ack 与 boundary power durable 区分、批事务提交前回滚/提交后重开存活，以及 `Flush(true)` 先于 SQLite durable watermark。
- 下一步：运行 A/B/C 三策略真实文件系统/SQLite 尖峰，写入 ignored `.tools/storage-spike/**` JSON，并以实际 `SELECT sqlite_version()` 定案 B-006 与 native WAL 风险。

## 2026-08-05 Storage durability spike 首轮红

- benchmark 小块的受控 build 已进入真实编译并通过，随后按原参数运行 `Category=StorageSpike`。
- 首轮真实尖峰退出码 1，用时 4m15s，1 total / 1 failed；A 路径已完整执行 100,000 次 `synchronous=FULL` 逐事件 autocommit，未降低负载冒充通过。
- 失败为 `DurabilitySpikeTests.cs:165` 的 `NullReferenceException`：A 的计数仍累加 `pending`，循环尾误入仅适用于批事务的 `transaction!.Commit()`，而 autocommit 的 transaction 按设计为 null。
- 最小修复仅将尾提交条件限定为 `batchSize > 1 && pending > 0`；100,000 事件、B 的 1024 batch、FULL 同步、断言与 durability 语义均未改变。
- 下一步：重编译并按同一命令重跑尖峰；只有 JSON 计数、实际 native SQLite 版本和恢复结果全部核对后才定案 B-006。

## 2026-08-05 Storage durability spike 最终绿与 B-006 决策

- 最终 benchmark build：退出码 0，0 warnings / 0 errors，用时 1.40 秒。
- 最终 `Category=StorageSpike`：退出码 0，1 passed / 0 failed / 0 skipped，用时 4m25s；证据位于 ignored `.tools/storage-spike/20260805-103129-485-26432/storage-durability-spike.json`。
- 环境：.NET runtime `10.0.10`，16 logical processors；`SELECT sqlite_version()` 实测 native SQLite `3.49.1`；`powerTestKind=storage-layer-contract`、`hardwareTerminalStatus=not-run`、`releaseEligible=false`。
- A autocommit mixed 100k：五项 accepted/ack/sqliteCommitted/powerDurable/recovered 均 100000，lost/corrupt=0；379.99 events/s，p50/p95=2459.8/3383.6us，13.025 bytes/event，263163.76ms。
- B batch1024 mixed 100k：五项均 100000，lost/corrupt=0；101442.24 events/s，p50/p95=6.7/8.1us，13.025 bytes/event，985.78ms。
- C mixed 100k：五项均 100000，spoolAppended=50000，lost/corrupt=0；113509.86 events/s，p50/p95=5.3/10.5us，22.369 bytes/event，880.98ms，boundary flush=12ms。
- C mouse 30k 等价负载：五项均 30000，spoolAppended=30000，lost/corrupt=0；192173.91 events/s，p50/p95=3.3/3.5us，45.229 bytes/event，156.11ms，boundary flush=7ms。
- B-006 契约层定案：A 不作为生产默认；键盘使用 B 有界批量聚合，鼠标使用 C CRC spool，boundary 必须先 `Flush(true)` 再提交 SQLite 目录水位；普通 sink ack 不等于 fsync，物理断电与真实硬件终验仍未运行。
- native 风险：SQLite `3.49.1` 命中官方 WAL-reset 受影响范围；唯一 writer/checkpointer 连接加 `Pooling=false` 仅规避已知触发条件，风险未消失，已新增 B-007，未静默换包。
- 下一步：请求主任务代跑全 solution restore/build/test、单独 Storage tests 与 self-contained publish；全部绿后再做白名单/Git 边界复核并提交，不 push。

## 2026-08-05 Storage durable watermark 发布 seam 修正

- 提交前数据正确性 gate 发现：writer 的旧 `FlushBoundary()` 在文件 `Flush(true)` 后、SQLite 目录 commit 前就返回公开 `SpoolDurableWatermark` 并推进 `PowerDurable`；若目录 commit 失败，调用者可见值可能领先 catalog，违反 B-006。
- 最小分层：writer 只向同程序集协调器返回 internal `SpoolFlushResult(fileFlushedCount, flushedLength)`，不再暴露或推进最终 `PowerDurable`；只有 `StorageDatabase.CommitSpoolBoundary` 在 SQLite commit 成功后才构造并返回公开 `SpoolDurableWatermark`。
- 新增真实 SQLite 外键失败 seam：文件 flush 成功后以未注册设备令目录事务失败；断言 spool 仍可恢复 1 条、catalog 为 0、调用者未取得最终 watermark。现有 commit 成功测试继续断言 catalog 与公开 `PowerDurable` 同为 1。
- benchmark 仍通过 `StorageDatabase.CommitSpoolBoundary` 读取最终水位，100,000/30,000、1024、FULL 和全部计数断言未改变。
- 下一步：先代跑 Storage.Tests build 与排除 `Category=StorageSpike` 的普通测试；小块全绿后再决定是否重跑 4m 尖峰，暂不提交。

## 2026-08-05 Storage durable seam 普通测试首轮红

- seam 分层后的 Storage.Tests build 退出码 0，0 warnings / 0 errors，用时 1.59 秒。
- 排除 spike 的普通测试退出码 1：11 total，10 passed / 1 failed / 0 skipped；失败为 `FileFlushBeforeDirectoryFailureDoesNotPublishFinalPowerDurability` 的 `IOException`。
- 根因：测试在 writer 仍持有写句柄时立即用新句柄执行 recovery；现有 `FileShare.Read` 正确拒绝与活跃写入并发恢复。模拟 SQLite 失败后的进程退出/重开必须先关闭 writer。
- 修复仅在测试中显式调用幂等 `writer.Dispose()` 后再 Recover；未放宽产品 FileShare、恢复校验、目录事务或 durable 断言。
- 下一步：重编译并重跑相同 11 个普通测试，保留本轮 10/11 红证据。

## 2026-08-05 Storage durable seam 普通测试最终绿

- 修复后的 Storage.Tests build 退出码 0，0 warnings / 0 errors，用时 1.46 秒。
- 排除 spike 的普通测试退出码 0：11 passed / 0 failed / 0 skipped，用时 306ms。
- 语义 gate 证明：文件 flush 成功但 catalog commit 失败时，调用者未取得最终 watermark；关闭 writer 后 spool 合法前缀可恢复，catalog 仍为空。成功路径只在 SQLite commit 后返回 `SpoolDurableWatermark`。
- 先前 4m25s JSON 仍可作为同负载性能参考，但公开 watermark 构造点已经改变，因此不再视为最终源码快照证据；必须按相同 100,000/30,000、1024、FULL 参数只重跑一次最终 spike。
- 下一步：等待最终源码 spike；随后再请求全 solution test 与 self-contained publish，暂不提交。

## 2026-08-05 Storage durable seam 最终源码 spike

- 最终语义分层源码的 `Category=StorageSpike` 退出码 0：1 passed / 0 failed / 0 skipped，用时 4m00s；权威 ignored JSON 为 `.tools/storage-spike/20260805-104501-272-18816/storage-durability-spike.json`。
- 环境仍为 native SQLite `3.49.1`，`hardwareTerminalStatus=not-run`、`releaseEligible=false`；B-007 不因本次绿色回归解除。
- A mixed 100k：五项计数均 100000，lost/corrupt=0；419.71 events/s，p50/p95=2426.1/2997.3us，13.025 bytes/event。
- B batch1024 mixed 100k：五项计数均 100000，lost/corrupt=0；96621.58 events/s，p50/p95=6.9/8.4us，13.025 bytes/event。
- C mixed 100k：五项计数均 100000，spoolAppended=50000，lost/corrupt=0；114469.09 events/s，p50/p95=5.2/10.6us，22.369 bytes/event，boundary flush=11ms。
- C mouse 30k 等价负载：五项计数均 30000，spoolAppended=30000，lost/corrupt=0；185268.10 events/s，p50/p95=3.3/3.5us，45.229 bytes/event，boundary flush=10ms。
- 结论：writer/file-flush 与 DB/post-commit watermark 分层后，B 键盘聚合 + C 鼠标 spool 契约的计数、恢复与性能结论保持成立；旧 4m25 JSON 仅保留为历史证据。
- 下一步：全 solution restore/build/test 与 self-contained publish；全部通过后做白名单、diff、测试数与 Git 状态复核，再提交独立 commit，不 push。

## 2026-08-05 Storage durability 最终统一回归

- `./.tools/dotnet/dotnet.exe restore InputTracker.sln`：退出码 0，7 项目均已是最新，用时 2.2 秒。
- `./.tools/dotnet/dotnet.exe build InputTracker.sln -c Release --no-restore`：退出码 0，7 项目生成，0 warnings / 0 errors，用时 1.43 秒。
- `./.tools/dotnet/dotnet.exe test InputTracker.sln -c Release --no-build --no-restore`：退出码 0；Input 29 + App 10 + Acceptance 12 + Storage 12 = 63 passed / 0 failed / 0 skipped，用时 4m30s。
- 全 solution test 的 spike JSON：`.tools/storage-spike/20260805-105247-814-27492/storage-durability-spike.json`；A/B/C 四组 accepted=sinkAcknowledged=sqliteCommitted=powerDurable=recovered，lost=0、corrupt=0。
- `./.tools/dotnet/dotnet.exe publish InputTracker.sln -c Release -r win-x64 --self-contained true`：退出码 0，7 项目完成 RID restore，App/Input/Storage 生成 win-x64 self-contained 目录，用时 6.1 秒。
- 最高可用判卷 `scripts/verify.ps1 -Stage InputCore`：退出码 0，`total=63`、`acceptance=12`、`failed=0`、`skipped=0`、`gates=23`；证据 `.tools/verification/20260805-185825-472/verification.json`。这只证明 Bootstrap/InputCore 未回归，Storage 正式 Acceptance gates 尚待 C2 基于稳定 API 落地。
- 判卷动态 Storage spike JSON：`.tools/storage-spike/20260805-105850-430-6940/storage-durability-spike.json`；四组计数继续闭合且 lost/corrupt=0，A=374.15、B=101652.95、C mixed=114491.47、C mouse30k=207908.85 events/s。
- 边界复核：`git diff --check` 退出码 0；变更仅在任务白名单，`.tools` 运行证据保持 ignored；产品 Storage 源码隐私禁止词/API 扫描无命中。
- 下一步：等待固定 C1 Git/GitHub 角色的独立只读白名单、依赖、隐私与测试单调性审查结论；审查前不 stage、不提交、不 push。

## 2026-08-05 C1 Storage durability 里程碑独立审查

- C1 只读审查批准提交：19 个意图文件全部位于 Storage 任务白名单；Acceptance/verify、App/Input/UI/portable、LICENSE 与 `.github` 均未修改。
- 依赖边界：仅新增任务授权的 `Microsoft.Data.Sqlite 10.0.10`；传递依赖与 native SQLite `3.49.1` 已记录，B-007 未弱化。
- 隐私与语义：键盘 schema 只含聚合 allowlist；durable watermark 只在文件 flush 与 SQLite 目录 commit 均成功后公开，失败 seam 不发布最终水位。
- 测试单调性：测试属性由 46 增至 58，统一实际运行 63 passed / 0 failed / 0 skipped；未删除、skip 或弱化既有测试。
- 发布阻塞：native WAL 风险、物理突然断电与真实多设备/1000Hz 硬件仍明确阻塞最终发布，不因本里程碑提交解除。
- 处置：仅暂存审查批准的 19 个文件，执行 cached diff/name 复核后提交 `feat: add storage durability foundation`；不 push、不建 PR、不开始下一切片。

## 2026-08-05 Storage durability 第一里程碑主分支集成

- worker 提交：`codex/storage-stats-work@61b341f29948e255c793417f7bb5e852d82f9969`，19 files changed / 1741 insertions / 2 deletions，工作树 clean，未 push/PR。
- 主任务集成：在 `codex/storage-stats@56493f0` 执行 `git cherry-pick 61b341f...`；首次退出码 1，唯一冲突为 `PROGRESS.md` 的两侧追加记录，功能代码、solution、依赖与 `BLOCKED.md` 均无冲突。
- 冲突处置：保留主分支的 Input Core 合并与 C2 Storage 验收设计，同时完整保留 C5 的失败、修复、尖峰、统一回归及 C1 审查记录；`git diff --cached --check` 退出码 0，无冲突标记。
- `git cherry-pick --continue` 退出码 0，生成 `3180070 feat: add storage durability foundation`；19 files changed / 1742 insertions / 2 deletions，多出的 1 行为冲突合并时恢复 Markdown 段落空行。
- 结论：B-006 仅在契约层解除；B-007 native SQLite `3.49.1`、物理突然断电和真实多设备/1000Hz 硬件仍阻塞发布，未因集成改变。
- 下一步：在集成提交上只跑快速 Release build 与排除 `StorageSpike` 的 11 个 Storage 普通测试；不重复已冻结源码上全绿的 4 分钟尖峰。通过后提交本集成/索引记录，再开始正式压缩轨迹块切片。

## 2026-08-05 Storage durability 集成后快速红→绿

- 首次 `./.tools/dotnet/dotnet.exe build InputTracker.sln -c Release --no-restore`：沙箱内退出码 1，7 项目均在 Windows SDK 探测时报 `MSB4184`（拒绝读取 `C:\Users\Administrator\AppData\Local\Microsoft SDKs`）；源码未变。
- 同命令受控重跑：进入真实构建后退出码 1；既有 5 项目生成成功，新集成的 Storage/Storage.Tests 因主工作区尚无 `project.assets.json` 报 2 个 `NETSDK1004`，0 warnings / 2 errors。
- `./.tools/dotnet/dotnet.exe restore InputTracker.sln` 受控执行退出码 0，7 项目成功恢复，Storage 与 Storage.Tests 各约 223ms；未更改依赖版本或系统环境。
- 原 Release build 再跑退出码 0，7 项目全部生成，0 warnings / 0 errors，用时 1.93 秒。
- `./.tools/dotnet/dotnet.exe test tests/InputTracker.Storage.Tests/InputTracker.Storage.Tests.csproj -c Release --no-build --no-restore --filter "Category!=StorageSpike"` 退出码 0：11 passed / 0 failed / 0 skipped，用时 266ms。
- 结果：功能提交 `3180070` 在主工作区通过快速编译与非尖峰恢复/迁移/durable seam 回归；未重复已经在同一源码语义上完成并记录的 4 分钟尖峰。
- 下一步：提交仅含 `PROGRESS.md` 与 `docs/codex/thread-index.md` 的集成记录；随后向 C5 下发正式压缩轨迹块与恢复的独立切片。

## 2026-08-05 Storage 正式压缩块切片启动

- 分支准备：保留已审查的 `codex/storage-stats-work@61b341f` 不动，在 C5 可定位工作树从最新主任务点 `codex/storage-stats@67955e6` 新建 `codex/storage-chunks-work`，`git switch -c` 退出码 0。
- 任务边界：仅 `src/InputTracker.Storage/**`、普通 Storage tests、`docs/storage/**`、`PROGRESS.md`、`BLOCKED.md`；禁止新增依赖或修改 Acceptance/verify、App/Input/UI/portable、solution、LICENSE/.github。
- 目标：versioned Brotli 增量轨迹块、CRC32C + canonical SHA-256、严格 reader、tmp→flush→rename→SQLite commit 后公开水位，以及 orphan/missing/corrupt 的幂等恢复矩阵；不得抽样或删除过渡 spool/损坏文件。
- 风险门：先核对现有 spool record 是否覆盖 Input Core 全鼠标原始字段；不足则写 BLOCKED，不静默丢信息。native SQLite `3.49.1` 继续只允许唯一 writer/checkpointer 与 `Pooling=false`。
- 线程：固定 C5 `Storage Stats`（`019fd04e-21df-7482-a248-99deb1617a5f`）已收到完整任务卡，推荐 `terra/high`，可见且可定位。
- 下一步：C5 增量实现并回报精确代跑命令；完成真实 FS/SQLite 单测与统一回归后，由 C1 独立审查再提交，不 push。

## 2026-08-09 Storage chunk 第二检查点红→绿

- 断点恢复：主工作区 `codex/storage-stats@099e07e` clean；C5 工作区 `codex/storage-chunks-work@67955e6` 保留未提交增量，未重做 2026-08-05 已完成的 codec 绿基线。
- 首次目标 build 进入源码编译后退出码 1：`MouseChunkCatalog.cs(339,13)` 报 `CS1739`，具名参数 `recovered` 与 record 参数 `Recovered` 大小写不一致；0 warnings / 1 error，用时 3.21 秒。C5 仅修正该处并保留红证据。
- 数据正确性收口：catalog 增加程序目录 containment、任意区间重叠与 catalog↔catalog 重叠拒绝、设备/校准外键预检、逐 orphan 独立事务、受控 diagnostics；恶意格式算术溢出转为 `InvalidDataException`；新增 4096 事件 / 256KiB canonical / 1 秒及设备、校准、暂停、停止边界的无损 builder。
- 代跑 `./.tools/dotnet/dotnet.exe build tests/InputTracker.Storage.Tests/InputTracker.Storage.Tests.csproj -c Release --no-restore`：退出码 0，Input/Storage/Storage.Tests 三项目生成，0 warnings / 0 errors，用时 2.55 秒。
- 代跑 `./.tools/dotnet/dotnet.exe test tests/InputTracker.Storage.Tests/InputTracker.Storage.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MouseChunk"`：退出码 0，26 passed / 0 failed / 0 skipped，用时 871ms。
- 当前结论：chunk 检查点自动测试全绿，但切片尚未完成；C5 继续补精确 privacy allowlist、v1→v2 migration、文档与过渡 spool 清理决策 BLOCKED，未 stage/commit/push。
- 下一步：跑全部非 Spike Storage 普通测试；完成静态/白名单审查后由 C1 独立复核，再执行 solution/verify/publish 全回归与提交。

## 2026-08-09 首次可测试 UI 只读预审

- 固定 C6 `UI` 线程只读核对 `codex/storage-stats@099e07e`，未修改文件、未建分支、未提交/推送、未运行 GUI；推荐 `terra/high`。
- 证据：App 已真实创建并启动 `InputCaptureService(new InputCoreSink())`，但当前 sink 只做进程内计数，App 未引用 Storage，统计、热力图和轨迹均无真实查询面。
- 知情阻断：`MainWindow.xaml` 仍宣称“当前 bootstrap 版本不采集任何输入”，与实际 Raw Input 启动冲突；首次测试前必须修正，并明确区分采集中、已暂停、完整性故障与存储健康。
- 生命周期风险：标题栏关闭可能销毁唯一 WPF 窗口，而托盘仍尝试重新 `Show()`；本轮禁止 GUI，静态风险为 not-run，后续必须用真实桌面复现并修复。
- 首测最短链路定案：真实物理事件 → Input Core → App 侧 Storage adapter → 单 writer 持久化 → 同一 Storage 服务只读快照 → 总览/设备筛选/键盘热力图/逐设备未抽样轨迹回放；禁止 mock 数据冒充完成。
- 视觉证据规划：2 分辨率 × 2 DPI × 2 主题 × 3 页面至少 24 张，加高风险暂停/IntegrityFault 2 张；当前全部 not-run，不作 passed 声明。
- 下一步：Storage 先提供设备身份、写入/边界、健康水位、四周期查询和严格顺序轨迹流；前置 PR 合并后 C6 才从最新 main 开 UI 分支实现。

## 2026-08-09 首次测试 Portable 只读预审

- 固定 C7 `Portable` 线程只读核对主工作区、发布配置和生命周期，未修改文件、未建分支、未提交/推送、未运行 GUI/build/test/publish；推荐 `luna/medium`。
- 已有基础：win-x64 self-contained 多文件配置、程序目录写探针、同目录单实例、托盘和用户暂停；历史产物不含 Storage，不能作为当前测试包。
- 缺口：锁屏/休眠原因虽在状态机存在，但 App 未订阅真实系统事件；Startup、首次启动告知、配置/日志/备份路径和卸载说明均未实现。
- 首测发布线：Storage 接通后建立唯一程序目录路径服务；Raw Input 启动前完成首次告知；正常交互生命周期验证目录外零写入、零网络、零注册表/服务/提权；Startup 未实现期间保持零改动。
- 单实例契约风险：当前 mutex 按程序目录分域，两个绿色副本可并行采集；后续必须明确保留该行为或改为用户级唯一，不能静默重复统计。
- 当前全部真实锁屏/休眠、普通用户、Startup、ProcMon/网络与卸载残留检查为 not-run；B-003/B-005/B-007 未解除。
- 下一步：在 UI 前置合并后，C7 从最新 main 开独立 portable 分支实现首次告知、系统暂停桥、Startup 窄能力和卸载说明。
## 2026-08-05 Storage mouse chunk 第一小块

- 新增 version 1 chunk contract、delta/zigzag/varint canonical payload、BCL Brotli、compressed CRC32C 与 canonical SHA-256，以及严格 reader；真实 FS 测试使用 Input Core 的 relative/absolute、virtual desktop、完整 10-bit button mask、Int32 坐标极值与 signed wheel 极值证明逐字段映射。
- 主任务代跑目标 build 退出码 0：Input/Storage/Storage.Tests 成功，0 warnings / 0 errors，用时 4.73 秒；`MouseChunkCodecTests` 退出码 0：3 passed / 0 failed / 0 skipped，用时 67ms。
- 只读收口发现 canonical decoder 接受 overlong varint，且初版 `CompressionLevel.SmallestSize` 不符合低后台 CPU 优先级。已最小修正为拒绝非最短 varint、增加端到端 fail-closed 文件测试，并改为 `CompressionLevel.Fastest`；事件数、字段和压缩格式标识不变。
- 收口后沙箱目标 build 再次在 Windows SDK 探测时报既知 `MSB4184`（拒绝读取 `C:\Users\Administrator\AppData\Local\Microsoft SDKs`），未进入源码编译；已请求主任务代跑 4-test codec 小块，同时继续 schema v2/catalog/recovery。
- 主任务代跑收口后目标 build 退出码 0：Input/Storage/Storage.Tests 成功，0 warnings / 0 errors，用时 1.79 秒；`MouseChunkCodecTests` 退出码 0：4 passed / 0 failed / 0 skipped，用时 65ms。canonical 唯一编码与 `Fastest` 低 CPU 策略已进入绿基线。

## 2026-08-09 Storage mouse chunk catalog 首轮编译红

- 主任务目标 build 已进入真实编译并退出码 1：0 warnings / 1 error，用时 3.21 秒；`MouseChunkCatalog.cs(339,13)` 报 `CS1739`，具名参数 `recovered` 与 record 构造参数 `Recovered` 大小写不符。
- 最小修复仅把该具名参数改为 `Recovered: true`；未改变 schema、catalog、recovery 或断言语义。继续完成路径 containment、区间重叠、独立 orphan 恢复与 bounded sealer 后再请求代跑。

## 2026-08-09 Storage mouse chunk 第二检查点绿

- 沙箱目标 build 仍仅在 Windows SDK 探测时报既知 `MSB4184`，未进入源码编译；主任务按相同源码快照代跑目标 build 退出码 0，Input/Storage/Storage.Tests 三项目生成，0 warnings / 0 errors，MSBuild 2.55 秒（外层 2.9 秒）。
- `FullyQualifiedName~MouseChunk` 定向测试退出码 0：26 passed / 0 failed / 0 skipped，测试持续 871ms（外层 2.5 秒）。该绿快照覆盖 codec、路径 containment、commit/recovery 区间与 FK、独立 orphan 恢复、DB 失败后补索引、catalog 重叠、builder 边界、迁移 schema 方法；随后新增的精确 privacy 反射/source allowlist 尚待下一轮构建回归。
- 后续只读预审契约已记录但不在本切片实现：Storage 继续不引用 Input，由 App adapter 映射 storage-native DTO；下一独立切片提供单 owner 串行写入/查询、设备观察、键盘 transition、完整鼠标与生命周期 boundary、health/watermark、范围聚合、严格顺序无抽样流、显式时区周期及 makeCode+prefix。UI 不得另开 checkpoint-capable 连接。

## 2026-08-09 Storage privacy 精确范围首轮红

- 主任务目标 build 退出码 0，0 warnings / 0 errors，MSBuild 1.23 秒；随后非 Spike Storage 回归退出码 1：43 total，41 passed / 2 failed / 0 skipped，测试 duration 1 秒、进程 wall 2.8 秒。两处均为新增隐私测试范围误报：键盘聚合测试扫描了新增且合法的 mouse sequence 列；SQL allowlist 把 SQLite `TEXT` 类型关键字当成名为 `Text` 的持久字段。
- 最小修正不删除或放宽任何禁止项：键盘禁字段仍完整检查 `keyboard_daily` 的六列；chunk SQL 禁止项改为精确匹配“禁止列名 + SQLite 类型”，公共 contract 反射 allowlist 与产品源码 identifier 扫描保持不变。待重编译并重跑同一 43 项普通测试。
- 同一命令红→绿：目标 build raw exit 0，Input/Storage/Storage.Tests 均生成，0 warnings / 0 errors，MSBuild 1.17 秒、进程 wall 1.6 秒；非 Spike Storage tests raw exit 0，43 passed / 0 failed / 0 skipped，test duration 796ms、进程 wall 2.1 秒。
- 下一步：完成当前 chunk slice 的静态与白名单自审；不扩展 App 接线、统计查询、导出或删除，不 stage/commit/push。自审和必要窄修通过后等待主任务安排 C1 独立审查与全量 solution/verify/publish 回归。

## 2026-08-09 Storage mouse chunk 冻结源码统一回归

- `dotnet restore InputTracker.sln` raw exit 0：1 个项目实际还原、6 个项目已是最新，进程 wall 2.7 秒。
- 首次 solution build 后发现 builder 仍有最后一处窄修，主任务主动终止随后运行的旧二进制全量 test；该次没有最终证据价值，不计为红或绿。源码冻结后重新 build raw exit 0：7 个项目生成，0 warnings / 0 errors，MSBuild 1.02 秒、进程 wall 1.4 秒。
- 冻结源码的 full solution test raw exit 0：App 10 + Input 29 + Acceptance 12 + Storage 44 = 95 passed / 0 failed / 0 skipped；Storage 持续 6m38s，进程 wall 400.9 秒。
- 最新 spike 证据为 `.tools/storage-spike/20260809-123522-798-26236/storage-durability-spike.json`，native SQLite `3.49.1`；四组 run 的 `accepted=sinkAcknowledged=sqliteCommitted=powerDurable=recovered`，分别为 100000 / 100000 / 100000 / 30000，且 `lost=0`、`corrupt=0`。`releaseEligible=false`、真实硬件与物理断电仍为 `not-run`。
- `scripts/verify.ps1 -Stage InputCore` raw exit 0：SDK `10.0.302`，95 total / 12 acceptance / 0 failed / 0 skipped，23 gates，进程 wall 222.6 秒；证据为 `.tools/verification/20260809-204253-505/verification.json`。
- solution `win-x64 --self-contained true` publish raw exit 0：7 个项目完成 RID restore，App/Input/Storage 均生成 publish 输出，进程 wall 4.1 秒。
- 下一步：保持源码冻结与未暂存状态，完成只读静态/白名单自审，等待 C1/C2；不得因本次回归解除 B-007、过渡 spool 清理、物理断电或真实硬件阻塞。

### 最终快照证据时序更正

- 后续核对发现同秒竞态：当前测试源码已包含 `[Fact] RejectedRecordDoesNotMutateBufferedState`，源码 mtime 为 `12:33:35Z`，上一轮 build DLL mtime 为 `12:33:36Z`，但该 DLL 的 `--list-tests` 仅列出 44 项且缺少该测试。这证明编译开始读取源码时最后窄修尚未进入构建输入。
- 因此，上述 solution 95 项与 verify 95 项只记录为旧 DLL 快照绿，不得冒充当前源码最终证据；publish 同样只证明该旧构建快照可发布。
- `src/**` 与 `tests/**` 自此绝对冻结。最终证据必须重新 build，并先确认 `list-tests=45`、非 Spike 项为 44，再执行完整 solution test、InputCore verify 与 win-x64 self-contained publish。

## 2026-08-09 Storage mouse chunk canonical SHA-256 独立反向验证

- C1 已批准产品 slice；C2 只读审查指出既有 payload bit flip 会先命中 compressed CRC32C，尚未独立证明 footer canonical SHA-256 被篡改时 reader fail closed。
- 仅在 `MouseChunkCodecTests.cs` 新增一个 `[Fact]`：保持 compressed payload 与其 CRC32C 原值不变，只翻转 footer canonical SHA-256 的一位，并断言 `MouseChunkReader.Read` 抛出 `InvalidDataException`。产品、helper、既有断言及其他测试文件均未修改。
- 预期重新构建后非 Spike Storage 为 45 项、完整 Storage 为 46 项、solution 为 97 项。测试文件现已再次绝对冻结，等待主任务代跑构建与回归。

## 2026-08-09 Storage mouse chunk 最终冻结快照回归

- `InputTracker.Storage.Tests` 强制 `Rebuild` raw exit 0：0 warnings / 0 errors，1.34 秒；`MouseChunkCodecTests` 为 7 passed / 0 failed / 0 skipped，37ms。
- full solution test raw exit 0：App 10 + Input 29 + Acceptance 12 + Storage 46 = 97 passed / 0 failed / 0 skipped；Storage 持续 4m14s，进程 wall 257.1 秒。
- `scripts/verify.ps1 -Stage InputCore` raw exit 0：SDK `10.0.302`，97 total / 12 acceptance / 0 failed / 0 skipped，23 gates，进程 wall 239.8 秒；证据为 `.tools/verification/20260809-210244-080/verification.json`。
- solution `win-x64 --self-contained true` publish raw exit 0：7 个项目完成 RID restore，App/Input/Storage 均生成 publish 输出，进程 wall 3.0 秒。
- 最新 spike 证据为 `.tools/storage-spike/20260809-130306-146-2968/storage-durability-spike.json`：native SQLite `3.49.1`、`releaseEligible=false`、真实硬件 `not-run`；四组 run 计数分别为 100000 / 100000 / 100000 / 30000，且 `accepted=sinkAcknowledged=sqliteCommitted=powerDurable=recovered`、`lost=0`、`corrupt=0`。
- 同秒增量 build 竞态已闭环：旧 DLL 缺少 `RejectedRecordDoesNotMutateBufferedState`，旧 solution/verify 95 项只作历史快照证据；强制 Rebuild 后先确认非 Spike 44 项，随后加入独立 canonical SHA-256 footer 测试，最终 Storage 46 / solution 97 项才是本 slice 的权威冻结快照。
- C2 结论：本 slice 的单元验收充分，但不代表最终 Storage 验收。轨迹块非零红命令必须留待后续 C2 Storage Acceptance seam；当前不修改 `verify`/Acceptance，也不伪报该红→绿已经完成。
- 下一步：保持所有产品与测试文件绝对冻结，不 stage/commit，等待最终提交指令；B-007、物理断电、真实硬件及过渡 spool 清理阻塞均保持不变。

## 2026-08-09 Storage mouse chunk 主分支集成

- C5 冻结切片提交为 `ae21717 feat: add versioned mouse chunk storage`：15 files changed / 2703 insertions / 11 deletions，工作树 clean，未 push/PR。
- 主任务在 `codex/storage-stats@0e05ac0` 执行 `git cherry-pick ae21717` 首轮 raw exit 1；唯一冲突为 `PROGRESS.md` 的双方追加记录，产品、测试、schema 与格式文档均自动合并。
- 冲突解决完整保留主分支的 Storage 检查点、UI/Portable 预审，以及 C5 的 chunk 红→绿、竞态更正与最终 97 项证据；`git diff --cached --check` exit 0、无未解决文件，`git cherry-pick --continue` exit 0，生成 `ff1c250 feat: add versioned mouse chunk storage`。
- 集成后快速 Release build raw exit 0：7 项目生成，0 warnings / 0 errors，MSBuild 4.41 秒；非 Spike Storage 回归 raw exit 0：45 passed / 0 failed / 0 skipped，996ms。
- 结论：versioned mouse chunk、bounded builder、v2 catalog/migration、CRC32C/SHA-256 与 fail-closed recovery 已进入主任务分支；这不是最终 Storage、硬件或发布验收，B-003/B-005/B-007/B-008 与物理断电继续阻塞。
- 下一步：从当前集成点开始独立实现 Storage 单 owner 写入/查询服务、设备观察、键盘日聚合、严格无抽样轨迹流与显式时区周期，优先打通首次可测试的真实 Input→Storage→UI 链路；C2 final Storage gate 等查询/导出/恢复/删除 API 稳定后落地。

## 2026-08-09 GitHub 同步三次失败

- 按 `github:yeet` 发布门槛核对：主工作树 clean，当前分支 `codex/storage-stats@40067f2`，远端为 `https://github.com/LinkStartR/InputTracker.git`；`gh 2.93.0` 已认证账号 `HorizonZhi`，相对 `origin/main` 为 7 笔已审查 Storage 提交。
- 第一次 `git push -u origin codex/storage-stats` raw exit 1，54.4 秒后 HTTPS 连接被重置；第二次相同 push raw exit 1，21.4 秒后无法连接 GitHub 443；第三次只对该命令指定 HTTP/1.1，raw exit 1，21.4 秒后仍无法连接 GitHub 443。
- 结果：连续三次同一外部阻断已写入 `BLOCKED.md` 顶部 B-009；本地提交与工作树未受影响，未创建远端分支或 draft PR，也未伪报成功。
- 下一步：停止网络重试，继续不依赖 GitHub 的 Storage 写入/查询实现；外网恢复后从同一提交重试 push 并创建 draft PR。

## 2026-08-09 Storage Service 替代线程启动

- 原 C5 `Storage Stats`（`019fd04e-21df-7482-a248-99deb1617a5f`）在 chunk 完成后接收下一切片时连续三次 systemError，第三次连只读 Git 探针都未执行；确认旧 worktree 仍 clean 且停在 `codex/storage-chunks-work@ae21717`，未污染新切片。
- 主任务创建同项目替代线程 `Storage Service`（`019fe6b9-dc32-7fd0-9c36-b8b2514a67af`），worktree `C:\Users\Administrator\.codex\worktrees\51f2\InputTracker 20260704` 已在界面可见且可定位；推荐 `terra/high`，环境使用可用默认模型与 high 思考。
- 分支原始结果：创建前 detached `6a6a5ea`、工作树 clean；`git switch -c codex/storage-service-work` 成功，创建后分支为 `codex/storage-service-work`、HEAD `6a6a5ea232b04372c1539643b4f4a173a0138afc`、工作树 clean。
- 范围：仅 Storage 产品/普通测试/docs 与 PROGRESS/BLOCKED；第一检查点为 storage-native contracts、schema v3 与设备映射，禁止重做 durability/chunk、App/Input/Acceptance/verify/依赖变更。
- 下一步：替代线程完成第一检查点后请求主任务代跑 build 与非 Spike tests；绿后继续单 owner ingestion、四周期键盘聚合和严格无抽样鼠标查询。

## 2026-08-09 Storage A 切片恢复与 UI 阻断修复并行启动

- C8 在 `codex/storage-service-work@857671f` 实现 A 切片时发生一次 `systemError`；已确认未提交现场完整保留为 `StorageContracts.cs`、`StorageDatabase.cs` 与新增 `KeyboardAggregation.cs`，schema v4 草稿已撤销，未回滚或重做第一检查点。
- 主任务从 C8 创建同目录恢复线程 C9 `019fe6fd-b748-7713-878a-37b8a43ce1b3`，标题仍为 `Storage Service`，已确认 active、可见且可定位；继续冻结 32 transitions / 64 rows / 250ms 主动 timer、`store_metadata` 水位、`SQLiteCommitted` 范围和 fail-closed 生命周期。
- 同时向既有可见 C6 `UI` 下发低耦合首测阻断修复：只纠正实际采集与过期隐私文案冲突、采集状态展示及关闭到托盘生命周期，不触碰 Input/Storage 核心，不用 mock 统计冒充真实链路。
- `docs/codex/thread-index.md` 已更新 C8/C9 替换关系与 C6 状态；`git diff --cached --check` exit 0，提交 `7035db7 docs: register storage recovery and ui work`，提交后主工作树 clean。
- 下一步：等待 C9 与 C6 各自冻结源码；主任务分别代跑目标 Release build/test，红则原样回传并最小修复，绿后交 C1/C2 只读审查再独立提交。GitHub B-009 仍有效，不重试 push。
