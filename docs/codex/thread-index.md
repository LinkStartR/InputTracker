# Codex 线程索引

最后更新：2026-08-09

- 项目目标：Windows 10/11 x64 本地绿色版 InputTracker。
- 工作流：完整编排；前置 PR 依赖串行，低耦合准备工作可并行。
- 当前阶段：Input Core PR #2 已合并为 `main@f6136c3`；versioned mouse chunk 已集成为 `codex/storage-stats@ff1c250`，下一切片打通 Storage 写入/查询与首次可测试真实链路；UI/Portable 首测路径只读预审已完成。

| ID | 子任务标题 | 负责事项 | 状态 | 范围 | 分支 / PR / 提交 | 可见或可定位 | 推荐模型 / 思考深度 | 成本理由 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Git/GitHub 同步 | 状态、意图文件、提交与 PR 协调 | 常驻 | Git 状态、分支、PR | chunk worker `ae21717` 审查通过；主任务集成 `ff1c250` | 是：`019fd04e-0c1c-77c2-ad74-c25eb4298b09` | luna / medium | 常规 Git 卫生；冲突时升级 | 15 文件边界、格式/恢复/隐私与测试单调性通过；B-007/B-008 和真实硬件仍待验 |
| C12 | Git/GitHub 同步 | UI 阻断修复提交前只读 Git 审查 | 进行中 | `codex/ui` 未提交 11 文件 | `codex/ui@f6136c31` | 是：`/root/git_review` | luna / medium | 常规白名单与提交边界 | 不修改/stage/commit/push；核对 16/0/0 冻结快照及越界依赖、测试、权限风险 |
| C13 | 用户测试与下一步建议 | UI 阻断修复提交前只读 QA | 进行中 | `codex/ui` 状态披露与关闭生命周期 | `codex/ui@f6136c31` | 是：`/root/ui_qa` | terra / high | 用户可见隐私与 WPF 生命周期 | 复核动态披露、SessionEnding 与 16/0/0；GUI/真实注销/硬件不得冒充通过 |
| C2 | 用户测试与下一步建议 | 验收、反向验证、人工测试与下一步 | chunk slice 只读验收完成 | `tests/Acceptance/**`, `scripts/verify.ps1`, QA 文档 | InputCore `03985a4`；Storage final gate/API seam 尚未落文件 | 是：`019fd04e-0c1c-77c2-ad74-c27a65676fd6` | terra / high | 跨模块、权限、迁移和平台矩阵 | 现有 slice 单元验收充分但非 final；等 ingestion/query/export/delete API 稳定后落 Storage Acceptance 与非零 corruption 红→绿 |
| C3 | Bootstrap | 最小 WPF、CI、单实例、托盘暂停 | 已集成 | `src/**`, 常规 `tests/**`, `.github/**`, solution/Directory 文件 | PR #1 已合并，merge `1fc6177` | 是：`019fd04e-0c87-71e0-b2e1-bfa363a691df` | gpt-5.6-sol / xhigh | 首个架构与权限边界 | 自动/CI/受控桌面状态链通过；普通用户可视启动/UAC/截图仍为 B-005 |
| C4 | Input Core | Raw Input、多设备、有界队列 | 已合并 | 输入模块及普通测试 | PR #2，merge `f6136c3` | 是：`019fd04e-0c7c-7c22-9135-1b53679805f1` | sol / high | 并发、设备身份、不丢事件 | 自动化进入 main；真实多设备/1000Hz 终验仍为 B-003 |
| C5 | Storage Stats | 轨迹块、SQLite、统计、导出、恢复删除 | chunk 已集成；后续线程 systemError | `src/InputTracker.Storage/**`、普通 Storage tests/docs | worker `ae21717`；主任务集成 `ff1c250` | 是：`019fd04e-21df-7482-a248-99deb1617a5f` | terra / high | 数据安全、迁移、时区 | chunk 最终冻结 solution 97/0/0、verify 97/12/23；下一切片连续三次 systemError，旧 worktree clean，改由 C8 续作 |
| C8 | Storage Service | 单 owner 写入、设备映射、周期聚合与只读查询 | 第一检查点已提交；A 切片 systemError | `src/InputTracker.Storage/**`、普通 Storage tests/docs | `codex/storage-service-work@857671f`；3 文件未提交现场保留 | 是：`019fe6b9-dc32-7fd0-9c36-b8b2514a67af` | terra / high | 迁移、隐私、时区与数据正确性 | contracts/schema v3/device catalog 经 C1/C2 放行，non-Spike 67/0/0；A 切片中断后由 C9 同目录续作，禁止重做 |
| C9 | Storage Service | 从 C8 现场续作单 owner 与键盘 ingestion A 切片 | 进行中 | `src/InputTracker.Storage/**`、普通 Storage tests/docs | `codex/storage-service-work@857671f` + 未提交现场 | 是：`019fe6fd-b748-7713-878a-37b8a43ce1b3` | terra / high | 同目录恢复，避免丢失或重做 | schema v4 已撤销；冻结 32 transitions / 64 rows / 250ms timer、SQLiteCommitted 水位与 fail-closed 生命周期；B-007/B-008/B-009/B-010 未解除 |
| C11 | Storage Service | 从 C9 保留现场完成单 owner 与键盘 ingestion A 切片 | 进行中 | Storage Service 白名单与普通 Storage tests/docs | `codex/storage-service-work@857671f` + 未提交现场 | 是：`/root/storage_service` | terra / high | 数据正确性、时区边界与故障关闭 | 禁止重做；保持 schema v3，完成冻结契约后由主任务代跑 non-Spike 并交 C1/C2 审阅 |
| C14 | 用户测试与下一步建议 | Storage Service A 提交前 Git/数据正确性只读预审 | 进行中 | 9 个 Storage 白名单文件 | `codex/storage-service-work@857671f` | 是：`/root/storage_review` | terra / high | SQLite 水位、时区拆分与故障关闭 | 分开报告 Git 边界与数据正确性；91/0/0 不代表 Spike、物理断电或最终 Storage 验收 |
| C6 | UI | 页面、筛选、热力图、轨迹回放、主题 | 首测隐私文案/托盘生命周期阻断修复进行中 | WPF UI 与视觉证据 | 从本地最新已合并 `main` 建 `codex/ui`；未提交/未推送 | 是：`019fd04e-238f-7c11-b854-4b0e4298228f` | terra / high | 跨页面状态与缩放矩阵 | 本切片只修真实采集披露、采集状态和关闭到托盘；GUI/截图仍 not-run，真实统计继续依赖 Storage |
| C10 | UI | 从 C6 冻结现场窄修状态披露与系统关机边界 | 进行中 | `src/InputTracker.App/**`、普通 App tests、`PROGRESS.md` | `codex/ui@f6136c31` + 未提交现场 | 是：`/root/ui_fix` | terra / high | WPF 生命周期、用户可见隐私状态 | C2 阻断要求 Running/StartFailed/Rejected 披露一致，并使 SessionEnding 进入受控退出；禁止改 Acceptance/verify |
| C7 | Portable | 自包含绿色发布、启动授权、锁屏与卸载 | 首测边界只读预审完成，待 UI 合并 | 发布、便携性、说明与测试 | 尚未建实现分支/提交；只读目标 `codex/storage-stats@099e07e` | 是：`019fd04e-2416-7e50-99c7-f8be6a5a8367` | luna / medium | 发布与权限边界明确 | 已确认锁屏/休眠、Startup、首次告知与卸载尚缺；当前保持 Startup 零写入，真实生命周期 not-run |

## 协调说明

- 共享文件由主任务协调，子任务不得越过范围。
- `tests/Acceptance/**` 与 `scripts/verify.ps1` 仅 C2 修改。
- 每个功能分支必须从最新 `main` 开始；前置 PR 合并后再 rebase 后继工作。
- 未确认可见或可定位前，不向子任务派发关键实现。
