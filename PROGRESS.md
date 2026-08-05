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
