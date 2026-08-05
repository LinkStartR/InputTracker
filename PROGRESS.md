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
