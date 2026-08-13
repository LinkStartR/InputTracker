# Codex 线程索引

最后更新：2026-08-13

- 项目目标：Windows 10/11 x64 本地绿色版 InputTracker。
- 工作流：完整编排；前置 PR 依赖串行，低耦合准备工作可并行。
- 当前阶段：Input Core PR #2 已合并为 `main@f6136c3`；Storage 写入/查询与 UI 首次真实链路已在 `codex/ui-storage-integration` 集成并通过本地判卷，等待第一次用户现场测试；Portable、删除/备份恢复和终验仍待完成。

| ID | 子任务标题 | 负责事项 | 状态 | 范围 | 分支 / PR / 提交 | 可见或可定位 | 推荐模型 / 思考深度 | 成本理由 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Git/GitHub 同步 | 状态、意图文件、提交与 PR 协调 | 常驻 | Git 状态、分支、PR | `codex/ui-storage-integration` 已 push；draft PR #3 | 是：`019fd04e-0c1c-77c2-ad74-c25eb4298b09` | luna / medium | 常规 Git 卫生；冲突时升级 | GitHub 网络已恢复；PR #3 为不可直接合并的依赖栈，B-007/B-008 和真实硬件仍待验 |
| C12 | Git/GitHub 同步 | UI 阻断修复提交前只读 Git 审查 | 已完成 | `codex/ui` 11 文件 | `codex/ui@3c84012` | 是：`/root/git_review` | luna / medium | 常规白名单与提交边界 | 最终增量复核通过；无越界依赖、测试或权限风险，允许主任务提交 |
| C13 | 用户测试与下一步建议 | UI 阻断修复提交前只读 QA | 已完成 | `codex/ui` 状态披露与关闭生命周期 | `codex/ui@3c84012` | 是：`/root/ui_qa` | terra / high | 用户可见隐私与 WPF 生命周期 | 动态披露、Cancel→Hide、SessionEnding 受控退出与 16/0/0 通过；GUI/真实注销/硬件仍 not-run |
| C2 | 用户测试与下一步建议 | 验收、反向验证、人工测试与下一步 | chunk slice 只读验收完成 | `tests/Acceptance/**`, `scripts/verify.ps1`, QA 文档 | InputCore `03985a4`；Storage final gate/API seam 尚未落文件 | 是：`019fd04e-0c1c-77c2-ad74-c27a65676fd6` | terra / high | 跨模块、权限、迁移和平台矩阵 | 现有 slice 单元验收充分但非 final；等 ingestion/query/export/delete API 稳定后落 Storage Acceptance 与非零 corruption 红→绿 |
| C3 | Bootstrap | 最小 WPF、CI、单实例、托盘暂停 | 已集成 | `src/**`, 常规 `tests/**`, `.github/**`, solution/Directory 文件 | PR #1 已合并，merge `1fc6177` | 是：`019fd04e-0c87-71e0-b2e1-bfa363a691df` | gpt-5.6-sol / xhigh | 首个架构与权限边界 | 自动/CI/受控桌面状态链通过；普通用户可视启动/UAC/截图仍为 B-005 |
| C4 | Input Core | Raw Input、多设备、有界队列 | 已合并 | 输入模块及普通测试 | PR #2，merge `f6136c3` | 是：`019fd04e-0c7c-7c22-9135-1b53679805f1` | sol / high | 并发、设备身份、不丢事件 | 自动化进入 main；真实多设备/1000Hz 终验仍为 B-003 |
| C5 | Storage Stats | 轨迹块、SQLite、统计、导出、恢复删除 | chunk 与 Storage Service A 已集成；下一步 mouse ingestion | `src/InputTracker.Storage/**`、普通 Storage tests/docs | chunk `ff1c250`；Service A `9c88d1c` | 是：`019fd04e-21df-7482-a248-99deb1617a5f` | terra / high | 数据安全、迁移、时区 | 集成快照 solution 144/0/0、verify 144/12/23；B-007/B-008/B-009 与硬件/物理断电未解除 |
| C8 | Storage Service | 单 owner 写入、设备映射、周期聚合与只读查询 | 第一检查点已提交；A 切片 systemError | `src/InputTracker.Storage/**`、普通 Storage tests/docs | `codex/storage-service-work@857671f`；3 文件未提交现场保留 | 是：`019fe6b9-dc32-7fd0-9c36-b8b2514a67af` | terra / high | 迁移、隐私、时区与数据正确性 | contracts/schema v3/device catalog 经 C1/C2 放行，non-Spike 67/0/0；A 切片中断后由 C9 同目录续作，禁止重做 |
| C9 | Storage Service | 从 C8 现场续作单 owner 与键盘 ingestion A 切片 | 已由 C11 完成 | `src/InputTracker.Storage/**`、普通 Storage tests/docs | worker `2e009f4`；主任务 `9c88d1c` | 是：`019fe6fd-b748-7713-878a-37b8a43ce1b3` | terra / high | 同目录恢复，避免丢失或重做 | schema v3；32 transitions / 64 rows / 250ms、SQLiteCommitted 与 fail-closed 已集成 |
| C11 | Storage Service | 从 C9 保留现场完成单 owner 与键盘 ingestion A 切片 | 已完成并集成 | 9 个 Storage 白名单文件 | worker `2e009f4`；主任务 `9c88d1c` | 是：`/root/storage_service` | terra / high | 数据正确性、时区边界与故障关闭 | 92 non-Spike、solution 144/0/0、verify 144/12/23；未 push，B-009 有效 |
| C14 | 用户测试与下一步建议 | Storage Service A 提交前 Git/数据正确性只读预审 | 已完成 | 9 个 Storage 白名单文件 | `2e009f4` / `9c88d1c` | 是：`/root/storage_review` | terra / high | SQLite 水位、时区拆分与故障关闭 | timer/owner/64+1 durable prefix/frequency 四项阻断修复后放行；不代表 final Storage |
| C15 | Storage Service | mouse ingestion B：spool→chunk/catalog 与生命周期边界 | 文档冻结已提交；产品实施中 | 冻结 18 文件 Storage mouse ingestion 白名单 | `codex/storage-mouse-ingestion@6ff5a7a` | 是：`/root/storage_service` | terra / high | 原始轨迹不丢点、durable 水位与故障关闭 | C1/C14 放行自描述 header/v4/global sequence/uncertain-ack 契约；禁止 query/export/delete、Acceptance 与清理 B-008 副本 |
| C6 | UI | 页面、筛选、热力图、轨迹回放、主题 | 首个阻断修复已提交并完成首次用户桌面测试；完整 UI 待 Storage 查询 | WPF UI 与视觉证据 | `codex/ui@3c84012`，未推送 | 是：`019fd04e-238f-7c11-b854-4b0e4298228f` | terra / high | 跨页面状态与缩放矩阵 | 16/0/0；用户反馈启动/托盘/暂停恢复/退出流程都 OK，无截图/逐步日志；统计/热力图/回放继续依赖 Storage |
| C10 | UI | 从 C6 冻结现场窄修状态披露与系统关机边界 | 已完成 | `src/InputTracker.App/**`、普通 App tests、`PROGRESS.md` | `codex/ui@3c84012` | 是：`/root/ui_fix` | terra / high | WPF 生命周期、用户可见隐私状态 | C1/C2 放行并由主任务提交；未 push/PR，B-009 有效 |
| C7 | Portable | 自包含绿色发布、启动授权、锁屏与卸载 | 首测边界只读预审完成，待 UI 合并 | 发布、便携性、说明与测试 | 尚未建实现分支/提交；只读目标 `codex/storage-stats@099e07e` | 是：`019fd04e-2416-7e50-99c7-f8be6a5a8367` | luna / medium | 发布与权限边界明确 | 已确认锁屏/休眠、Startup、首次告知与卸载尚缺；当前保持 Startup 零写入，真实生命周期 not-run |
| C21 | Input Device Identity | SetupAPI/ConfigMgr 结构化瞬时身份与隐私边界 | 实现完成，待提交 | `src/InputTracker.Input/**`、普通 Input tests/docs | `codex/input-device-identity@f6136c3` 加未提交切片 | 是：`/root/input_device_identity` | sol / high | 设备身份与隐私，影响历史映射正确性 | 37/0/0；不从 Instance ID 猜 serial，真实硬件仍 B-003 |
| C23 | Git/GitHub 同步 | Input 身份切片提交前只读 Git/隐私审查 | 已完成 | 10 个 Input 身份意图文件 | `codex/input-device-identity@f6136c3` 加未提交切片 | 是：`/root/git_input_identity_review` | luna / medium | 常规白名单与 API 边界 | 白名单/worker-only/隐私/序列号来源全通过，放行精确提交；B-009 禁止 push |
| C20 | UI Storage Adapter | Input→Storage 接线、真实统计/轨迹 UI 与退出控制 | 已完成，待主任务提交 | `src/InputTracker.App/**`、普通 App tests | `codex/ui-storage-integration` 未提交切片 | 是：`/root/ui_storage_adapter` | terra / high | 跨 Input/Storage/UI 的数据正确性与生命周期 | App 30/0/0；全量与判卷均绿；真实 GUI/硬件/视觉仍 pending |
| C24 | Git/GitHub 同步 | UI Storage Adapter 最终白名单与行为复审 | 已完成 | App/UI 未提交切片 | `codex/ui-storage-integration` | 是：`/root/git_storage_p0_review` | luna / medium | 精确 Git 与用户可见事实 | absolute/relative 分段修复后放行；不代表最终视觉验收 |
| C25 | 用户测试与下一步建议 | UI Storage Adapter 数据正确性复审 | 已完成 | App→Storage 生命周期与有界查询 | `codex/ui-storage-integration` | 是：`/root/storage_p0_review` | terra / high | 暂停、退出、设备与轨迹完整性 | 退出兜底、resume 回滚、unknown removal 与百万点边界放行；硬件终验保持 pending |
| C26 | Storage Stats | CSV/PNG 导出、备份恢复与范围删除安全协议 | 进行中 | `src/InputTracker.Storage/**`、普通 Storage tests、`docs/storage/**` | `codex/ui-storage-integration@2fec60d` 未提交切片 | 是：`/root/storage_completion` | terra / high；删除恢复复审 xhigh | 用户数据删除、恢复和格式一致性为最高风险 | 不操作真实数据；删除必须 preview+确认 token，无法安全处理块内范围则写 BLOCKED 并继续其他项 |
| C27 | Portable | 首次告知、Startup 授权、锁屏/挂起、数据位置和卸载 | 进行中 | `src/InputTracker.App/**`、普通 App tests、`docs/portable/**`、README | `codex/ui-storage-integration@2fec60d` 未提交切片 | 是：`/root/portable_completion` | luna / medium；系统生命周期 high | 常规本地功能，权限与会话边界需升级审查 | 不真实改本机 Startup；测试使用临时目录 seam，不写注册表、不联网 |

## 协调说明

- 共享文件由主任务协调，子任务不得越过范围。
- `tests/Acceptance/**` 与 `scripts/verify.ps1` 仅 C2 修改。
- 每个功能分支必须从最新 `main` 开始；前置 PR 合并后再 rebase 后继工作。
- 未确认可见或可定位前，不向子任务派发关键实现。
