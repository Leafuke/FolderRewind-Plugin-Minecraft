# Minecraft incremental Artifact：开发状态

实现：`MineRewind.World/Semantic/WorldIncrementalFormat.cs`；Host 适配：`MineRewind/V3/MinecraftSavesPlugin.Incremental.cs`。

要求 API 3.10，当前用本机隔离 feed 的 `3.10.0-p3.1` 包独立构建；不能捆绑 Abstractions DLL 或引用 Host 源码。现有 TransformerId 保留，参数 `representation=incremental-v1` 选择新格式。未发布，普通配置入口仍被 Host Gate 阻断。

完整目录引用 recipe/index/data packs，三层 DAG；不创建 Minecraft 私有版本链。未变文件不读源文件、不解码 chunk；变化 region 比较原始 payload 后决定解码与复用；外置 chunk 按最终 inventory 关联。原始 region 所有字节仍可还原，opaque 不等于 Absent。

数据 pack 64 MiB，metadata pack 8 MiB，残余块按 64 KiB 对齐。周期/70% 变化触发的 checkpoint 不重复捕获本次变化。保留放大统计计入 metadata pack 固定的额外数据依赖；当前计算逻辑字节，不能当作物理压缩率或 Cloud 存储量。

`IncrementalTests` 包含 sparse、mcc 单改/删除/内外转换、orphan、opaque、header/padding/tail、周期/大比例 checkpoint、取消、非法变化集。测试不是性能 Gate。Host 的 P3 计划、格式合同和验收记录是集成状态来源；P1/P2 的历史验收继续有效。

默认一致性仍复制整个 Source，使用可取消的有界流式复制。没有宣称总 I/O 为 O(changes)，也没有宣称复制过程是 Minecraft 原子快照。正常入口需先通过 Host 规定的真实世界安全和性能 Gate。
