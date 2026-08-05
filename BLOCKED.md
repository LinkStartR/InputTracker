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
- 处置：先完成自动化、采集证据脚本和可复现人工步骤；到达门槛时提交实际结果或保持阻塞。

## B-004：任务指定的 NuGet 依赖（已获任务书授权，待逐项落锁）

- bootstrap 预计需要：`Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`；WPF 与托盘优先使用框架自带能力。
- storage-stats 预计需要：`Microsoft.Data.Sqlite`；压缩优先使用 BCL，避免额外依赖。
- 约束：必须锁定明确版本并进入 restore/build/test；任何未列出的第三方包先追加到本文件，不静默引入。
