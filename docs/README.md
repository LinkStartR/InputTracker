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

2026-09-26 上一轮待测包为 `.tools/publish/ui-refresh-20260926-dce906e/`，对应源码提交 `dce906e`，用于检查新版窗口布局和图标。构建、435 项自动测试及发布包 `--self-check` 已通过；随后收到托盘退出需点击两次的反馈。旧包 `concise-device-name-test-20260918/` 含有运行数据，暂时保留。

2026-09-26 托盘退出测试包为 `.tools/publish/tray-exit-once-20260926-67986c0/`，对应修复提交 `67986c0`。应用测试 150 项、验收测试 32 项和发布包 `--self-check` 已通过；用户随后确认单次退出问题已验收。前一轮包暂留作对照。

2026-09-26 上一轮待测包为 `.tools/publish/first-run-rounding-20260926-38265e1/`，对应界面提交 `38265e1`，用于检查首次记录确认窗口的观感，以及主窗口和确认窗口的 8 像素圆角。应用测试 150 项、验收测试 32 项和发布包 `--self-check` 已通过；随后收到采集说明和字体观感的反馈。旧测试包暂留作对照。

2026-09-26 上一轮待测包为 `.tools/publish/copy-font-20260926-c8add6f/`，对应界面提交 `c8add6f`，用于检查精简后的采集说明和新版字体、字号。应用测试 150 项、验收测试 32 项和发布包 `--self-check` 已通过；随后收到开机启动说明仍冗长、跨屏移动后字体发糊的反馈。旧测试包暂留作对照。

2026-09-26 上一轮待测包为 `.tools/publish/startup-dpi-20260926-0bd39f5/`，对应修复提交 `0bd39f5`，用于检查开机启动状态文案和跨不同缩放比例显示器的清晰度。应用测试 152 项、验收测试 32 项和发布包 `--self-check` 已通过；发布版进程报告逐显示器 DPI 模式，编译后的清单包含 `PerMonitorV2`。随后用户反馈仍看到旧包中的标题副句；旧测试包暂留作对照。

2026-09-26 当前待测包为 `.tools/publish/single-line-header-20260926-ac832c6/`，对应界面提交 `ac832c6`。标题区保留单行产品名；应用测试 152 项及发布包 `--self-check` 已通过，发布包 DLL 已核对不含旧副句。实际桌面验收需从该新路径启动。鼠标按键页的线稿式布局效果图见 `docs/mouse-button-visual-concept-v3.png`，对应设计提交 `2fbca33`；图中次数为示意值，旧测试包未改动。

2026-09-27 当前待测包为 `.tools/publish/mouse-diagram-20260927-0641198/`，对应界面提交 `0641198`。鼠标按键页显示线稿鼠标及左键、右键、中键、侧键 X1、侧键 X2 的实际次数；应用测试 152 项、验收测试 32 项和发布包 `--self-check` 已通过，1280×800 与 900×650 窗口截图已检查。旧测试包暂留作对照。
