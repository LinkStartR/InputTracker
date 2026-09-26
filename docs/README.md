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

`.tools/publish/` 保存供人工操作的自包含测试包，属于待验收候选包，不代表正式发布。每轮交付记录包名、对应 Git 提交、测试目的和反馈；默认只保留当前待测包。确需保留旧包作对照时，记录原因并在下一轮重新检查。

新包准备好后，先检查旧包是否含有 `data/`、`backups/`、设置或其他运行数据，确定数据保留方式，再将不再需要的旧包移入回收站。不要把旧包的运行数据当作可重新构建的二进制清理，也不要把现有包直接用作破坏性验收的测试副本。自动验收生成的 `.tools/verification/` 快照是另一类证据，不计入人工测试包数量。
