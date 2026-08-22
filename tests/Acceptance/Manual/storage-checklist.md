# Storage 现场验收清单

自动化 Storage 阶段只证明公开查询、迁移、恢复、导出和当前删除阻断契约；它不替代突然断电或真实多设备终验。

- [ ] 记录 commit、Windows 版本、.NET SDK/runtime、CPU、内存和磁盘型号/文件系统。
- [ ] 两键盘两鼠标前后台连续采集至少 2 小时；记录每设备接口标识、硬件信息、身份置信度、预期/实际计数与零串线证据。
- [ ] 1000Hz 鼠标连续 30 分钟；记录原始、接收、落盘、durable watermark、恢复计数和每小时磁盘增长。
- [ ] 在独立测试数据副本执行进程强杀恢复；只声明已发布 durable prefix，无 gap、无重复。
- [ ] 突然断电证据必须标为 `physical`、`storage-layer-simulation` 或 `not-run`；后两者不得宣称物理断电通过。
- [ ] 检查 CSV 不含文字、窗口、进程、应用、接口路径、硬件 ID、序列号或 ContainerId；检查 PNG 尺寸、可读性与哈希。
- [ ] 创建备份，恢复到已知快照，并确认恢复前自动备份可再次恢复。
- [ ] 删除预览必须显示 `BlockedByCurrentFormat`；本阶段不得执行或宣称范围删除通过。

全部原始证据放在程序目录内的独立验收目录。任一真实终验未完成时，`releaseEligible` 必须为 `false`。
