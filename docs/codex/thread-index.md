# Codex 线程索引

最后更新：2026-08-05

- 项目目标：Windows 10/11 x64 本地绿色版 InputTracker。
- 工作流：完整编排；前置 PR 依赖串行，低耦合准备工作可并行。
- 当前阶段：Bootstrap PR #1 已合并为 `main@1fc6177`；Input Core 三个已审查实现提交已集成，等待 C2 阶段判卷。

| ID | 子任务标题 | 负责事项 | 状态 | 范围 | 分支 / PR / 提交 | 可见或可定位 | 推荐模型 / 思考深度 | 成本理由 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Git/GitHub 同步 | 状态、意图文件、提交与 PR 协调 | 常驻 | Git 状态、分支、PR | PR #1 已合并；Input Core `0a31520..a5aa404` 最终边界通过 | 是：`019fd04e-0c1c-77c2-ad74-c25eb4298b09` | luna / medium | 常规 Git 卫生；冲突时升级 | 三提交可按序集成，测试属性 `11→27→28→34`，真实硬件仍待验 |
| C2 | 用户测试与下一步建议 | 验收、反向验证、人工测试与下一步 | 执行中 | `tests/Acceptance/**`, `scripts/verify.ps1`, QA 文档 | `codex/input-core-acceptance@99690ce`，InputCore 判卷实现中 | 是：`019fd04e-0c1c-77c2-ad74-c27a65676fd6` | terra / high | 跨模块、权限、迁移和平台矩阵 | 追加百万计数、完整性故障、隐私、设备隔离 gate；真实硬件保持 not-run |
| C3 | Bootstrap | 最小 WPF、CI、单实例、托盘暂停 | 已集成 | `src/**`, 常规 `tests/**`, `.github/**`, solution/Directory 文件 | PR #1 已合并，merge `1fc6177` | 是：`019fd04e-0c87-71e0-b2e1-bfa363a691df` | gpt-5.6-sol / xhigh | 首个架构与权限边界 | 自动/CI/受控桌面状态链通过；普通用户可视启动/UAC/截图仍为 B-005 |
| C4 | Input Core | Raw Input、多设备、有界队列 | 兼容修补中 | 输入模块及普通测试 | worker `30814c0`、`1fa152a`、`a5aa404`；主分支 `7f5fda8`、`9c633d0`、`cb4c2da` | 是：`019fd04e-0c7c-7c22-9135-1b53679805f1` | sol / high | 并发、设备身份、不丢事件 | 正在补 descriptor-before-first-event；不得修改 C2 文件，真实硬件仍为 B-003 |
| C5 | Storage Stats | 轨迹块、SQLite、统计、导出、恢复删除 | 只读设计完成，等待依赖 | 存储统计模块及测试 | `codex/storage-stats`，未建实现分支 | 是：`019fd04e-21df-7482-a248-99deb1617a5f` | terra / high | 数据安全、迁移、时区 | 发现设备描述前置门；Input Core 修补并合并后再实现 |
| C6 | UI | 页面、筛选、热力图、轨迹回放、主题 | 待命 | WPF UI 与视觉证据 | `codex/ui` | 是：`019fd04e-238f-7c11-b854-4b0e4298228f` | terra / high | 跨页面状态与缩放矩阵 | 依赖 storage-stats PR 合并 |
| C7 | Portable | 自包含绿色发布、启动授权、锁屏与卸载 | 待命 | 发布、便携性、说明与测试 | `codex/portable` | 是：`019fd04e-2416-7e50-99c7-f8be6a5a8367` | luna / medium | 发布与权限边界明确 | 依赖 UI PR 合并 |

## 协调说明

- 共享文件由主任务协调，子任务不得越过范围。
- `tests/Acceptance/**` 与 `scripts/verify.ps1` 仅 C2 修改。
- 每个功能分支必须从最新 `main` 开始；前置 PR 合并后再 rebase 后继工作。
- 未确认可见或可定位前，不向子任务派发关键实现。
