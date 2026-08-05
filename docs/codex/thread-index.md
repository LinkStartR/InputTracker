# Codex 线程索引

最后更新：2026-08-05

- 项目目标：Windows 10/11 x64 本地绿色版 InputTracker。
- 工作流：完整编排；前置 PR 依赖串行，低耦合准备工作可并行。
- 当前阶段：Bootstrap 自动验收已集成并全绿；等待 C1 最终复查、push、draft PR 与人工托盘检查，C4-C7 等待依赖。

| ID | 子任务标题 | 负责事项 | 状态 | 范围 | 分支 / PR / 提交 | 可见或可定位 | 推荐模型 / 思考深度 | 成本理由 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Git/GitHub 同步 | 状态、意图文件、提交与 PR 协调 | 常驻 | Git 状态、分支、PR | `codex/bootstrap@e0b1c20`，draft PR #1 | 是：`019fd04e-0c1c-77c2-ad74-c25eb4298b09` | luna / medium | 常规 Git 卫生；冲突时升级 | 最终只读复查通过；分支已推送，CI 全绿，PR 保持 draft |
| C2 | 用户测试与下一步建议 | 验收、反向验证、人工测试与下一步 | 常驻 | `tests/Acceptance/**`, `scripts/verify.ps1`, QA 文档 | worker `7c44525`，主分支集成 `739e16a` | 是：`019fd04e-0c1c-77c2-ad74-c27a65676fd6` | terra / high | 跨模块、权限、迁移和平台矩阵 | Bootstrap：11 passed、0 skipped、11 gates；绿色路径反向验证按预期退出 1 |
| C3 | Bootstrap | 最小 WPF、CI、单实例、托盘暂停 | 部分通过 | `src/**`, 常规 `tests/**`, `.github/**`, solution/Directory 文件 | `codex/bootstrap@e0b1c20`，draft PR #1 | 是：`019fd04e-0c87-71e0-b2e1-bfa363a691df` | gpt-5.6-sol / xhigh | 首个架构与权限边界 | 自动/CI/受控桌面状态链通过；普通用户可视启动/UAC/截图为 B-005 |
| C4 | Input Core | Raw Input、多设备、有界队列 | 待命 | 输入模块及单元/集成测试 | `codex/input-core` | 是：`019fd04e-0c7c-7c22-9135-1b53679805f1` | sol / high | 并发、设备身份、不丢事件 | 依赖 bootstrap PR 合并 |
| C5 | Storage Stats | 轨迹块、SQLite、统计、导出、恢复删除 | 待命 | 存储统计模块及测试 | `codex/storage-stats` | 是：`019fd04e-21df-7482-a248-99deb1617a5f` | terra / high | 数据安全、迁移、时区 | 依赖 input-core PR 合并 |
| C6 | UI | 页面、筛选、热力图、轨迹回放、主题 | 待命 | WPF UI 与视觉证据 | `codex/ui` | 是：`019fd04e-238f-7c11-b854-4b0e4298228f` | terra / high | 跨页面状态与缩放矩阵 | 依赖 storage-stats PR 合并 |
| C7 | Portable | 自包含绿色发布、启动授权、锁屏与卸载 | 待命 | 发布、便携性、说明与测试 | `codex/portable` | 是：`019fd04e-2416-7e50-99c7-f8be6a5a8367` | luna / medium | 发布与权限边界明确 | 依赖 UI PR 合并 |

## 协调说明

- 共享文件由主任务协调，子任务不得越过范围。
- `tests/Acceptance/**` 与 `scripts/verify.ps1` 仅 C2 修改。
- 每个功能分支必须从最新 `main` 开始；前置 PR 合并后再 rebase 后继工作。
- 未确认可见或可定位前，不向子任务派发关键实现。
