# 文档导航

项目状态、从全新克隆构建和运行自动测试见[仓库 README](../README.md)。以下文档按用途查找；`docs/history/` 只记录历史，不作为当前产品契约。

## 使用与实现

- [绿色版使用、数据位置与卸载](portable/README.md)
- [输入设备身份与隐私边界](input/device-identity-v1.md)
- Storage：[写入与设备身份](storage/storage-service-v1.md)、[查询](storage/storage-query-v1.md)、[导出、备份、恢复与删除](storage/export-backup-delete-v2.md)、[App 离线恢复流程](storage/offline-restore-app-design.md)

## 验收

- [分阶段验收计划](acceptance-plan.md)
- 人工现场清单：[Bootstrap](../tests/Acceptance/Manual/bootstrap-checklist.md)、[输入硬件与性能](../tests/Acceptance/Manual/input-core-hardware-runbook.md)、[Storage](../tests/Acceptance/Manual/storage-checklist.md)、[Portable](../tests/Acceptance/Manual/portable-checklist.md)、[离线恢复](../tests/Acceptance/Manual/offline-restore-checklist.md)

人工清单与相应的证据 schema 保留在 `tests/Acceptance/Manual/`，验收代码和脚本直接检查这些路径。

## 历史记录

- [Storage 耐久性实验](history/storage-durability-spike.md)：已退役鼠标轨迹方案的实验记录，不能代替现行 Storage 契约。
- [本地测试包历史](history/local-test-package-history.md)：已交付测试包的来源、反馈与历史清理记录。

## 本地手动测试包

`.tools/publish/` 保存供人工操作的自包含测试包，属于待验收候选包，不代表正式发布。每轮交付记录包名、对应 Git 提交、测试目的和反馈；历次交付见[本地测试包历史](history/local-test-package-history.md)。

新包准备好后，先检查旧包是否含有 `data/`、`backups/`、设置或其他运行数据，确定数据保留方式，再将不再需要的旧包移入回收站。不要把旧包的运行数据当作可重新构建的二进制清理，也不要把现有包直接用作破坏性验收的测试副本。自动验收生成的 `.tools/verification/` 快照是另一类证据，不计入人工测试包数量。

2026-09-27 当前待测包为 `.tools/publish/mouse-side-source-swap-20260927-c9ed8a0/`，对应统计映射提交 `c9ed8a0`。鼠标图上方显示 X1、下方显示 X2，界面统计将原始 X2 次数显示为 X1、原始 X1 次数显示为 X2；总览页使用相同口径。应用测试 152 项、验收测试 32 项及发布包 `--self-check` 已通过。

2026-09-27 只读盘点发现本地仍有 11 个旧测试包，其中 8 个含 `data/`，部分还含设置文件。旧包按上述规则核对运行数据后再决定保留或清理；这些历史包的路径与反馈见[交付记录](history/local-test-package-history.md)。
