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
