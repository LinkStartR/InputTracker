# Codex 线程索引

最后更新：2026-08-05

- 项目目标：Windows 10/11 x64 本地绿色版 InputTracker。
- 工作流：完整编排；前置 PR 依赖串行，低耦合准备工作可并行。
- 当前阶段：Input Core PR #2 已合并为 `main@f6136c3`；Storage durability 基础已集成为 `codex/storage-stats@3180070`，继续实现正式压缩块、统计、导出与恢复删除。

| ID | 子任务标题 | 负责事项 | 状态 | 范围 | 分支 / PR / 提交 | 可见或可定位 | 推荐模型 / 思考深度 | 成本理由 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Git/GitHub 同步 | 状态、意图文件、提交与 PR 协调 | 常驻 | Git 状态、分支、PR | Storage durability 白名单审查通过；worker `61b341f` 已集成为 `3180070` | 是：`019fd04e-0c1c-77c2-ad74-c25eb4298b09` | luna / medium | 常规 Git 卫生；冲突时升级 | 19 文件边界、依赖、隐私与测试单调性通过；B-007 和真实硬件仍待验 |
| C2 | 用户测试与下一步建议 | 验收、反向验证、人工测试与下一步 | Storage 只读设计完成 | `tests/Acceptance/**`, `scripts/verify.ps1`, QA 文档 | InputCore `03985a4`；Storage gate/API 前置已回报，未落文件 | 是：`019fd04e-0c1c-77c2-ad74-c27a65676fd6` | terra / high | 跨模块、权限、迁移和平台矩阵 | 等 C5 定案 durable model 后才实现 Storage Acceptance；禁止 skip/todo |
| C3 | Bootstrap | 最小 WPF、CI、单实例、托盘暂停 | 已集成 | `src/**`, 常规 `tests/**`, `.github/**`, solution/Directory 文件 | PR #1 已合并，merge `1fc6177` | 是：`019fd04e-0c87-71e0-b2e1-bfa363a691df` | gpt-5.6-sol / xhigh | 首个架构与权限边界 | 自动/CI/受控桌面状态链通过；普通用户可视启动/UAC/截图仍为 B-005 |
| C4 | Input Core | Raw Input、多设备、有界队列 | 已合并 | 输入模块及普通测试 | PR #2，merge `f6136c3` | 是：`019fd04e-0c7c-7c22-9135-1b53679805f1` | sol / high | 并发、设备身份、不丢事件 | 自动化进入 main；真实多设备/1000Hz 终验仍为 B-003 |
| C5 | Storage Stats | 轨迹块、SQLite、统计、导出、恢复删除 | 正式压缩块实现中 | `src/InputTracker.Storage/**`、普通 Storage tests/docs | durability `61b341f`/主分支 `3180070`；当前 `codex/storage-chunks-work@67955e6` | 是：`019fd04e-21df-7482-a248-99deb1617a5f` | terra / high | 数据安全、迁移、时区 | 只做 versioned chunk、校验、原子提交和恢复；不删除过渡 spool，B-007 未解除 |
| C6 | UI | 页面、筛选、热力图、轨迹回放、主题 | 待命 | WPF UI 与视觉证据 | `codex/ui` | 是：`019fd04e-238f-7c11-b854-4b0e4298228f` | terra / high | 跨页面状态与缩放矩阵 | 依赖 storage-stats PR 合并 |
| C7 | Portable | 自包含绿色发布、启动授权、锁屏与卸载 | 待命 | 发布、便携性、说明与测试 | `codex/portable` | 是：`019fd04e-2416-7e50-99c7-f8be6a5a8367` | luna / medium | 发布与权限边界明确 | 依赖 UI PR 合并 |

## 协调说明

- 共享文件由主任务协调，子任务不得越过范围。
- `tests/Acceptance/**` 与 `scripts/verify.ps1` 仅 C2 修改。
- 每个功能分支必须从最新 `main` 开始；前置 PR 合并后再 rebase 后继工作。
- 未确认可见或可定位前，不向子任务派发关键实现。
