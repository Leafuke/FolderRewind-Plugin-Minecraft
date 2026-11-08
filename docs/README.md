# MineRewind 工程约束

这里只保留与源码共同审查的领域、格式和读取约束。操作指南归属独立网站；计划、测量、截图及验收报告写入忽略的 `artifacts/`。

- [公开契约与来源](../contracts/README.md)：精确 SDK、独立构建、正式来源及开发候选门禁。
- [布局事实与边界](architecture/world-layout-facts.md)：路径语法、身份依据、用途策略、计数与不完整读取。
- [读取与增量状态 v1](../contracts/minecraft-read-and-state-v1.md)：NBT／目录预算和可选诊断。
- [无损区块模型](architecture/chunk-semantic-foundation.md)：原始字节、语义比较及输入完整性。
- [Minecraft World Full v1](architecture/minecraft-world-full-v1.md)：只读兼容格式、写入退役及物化临时空间生命周期。
- [Minecraft World Incremental v1](architecture/minecraft-world-incremental-v1.md)：Full、Smart、checkpoint 和对象读取约束。
