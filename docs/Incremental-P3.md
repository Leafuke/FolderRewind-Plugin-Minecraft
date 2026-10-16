# Minecraft incremental Artifact：开发状态

实现：`MineRewind.World/Semantic/WorldIncrementalFormat.cs`；Host 适配：`MineRewind/V3/MinecraftSavesPlugin.Incremental.cs`。

要求 API 3.10，当前用基于契约源码摘要的本机隔离 feed 包独立构建；不能捆绑 Abstractions DLL 或引用 Host 源码。现有 TransformerId 保留，参数 `representation=incremental-v1` 选择新格式。未发布，普通配置入口仍被 Host Gate 阻断。

完整目录引用 recipe/index/data packs，三层 DAG；不创建 Minecraft 私有版本链。未变文件不读源文件、不解码 chunk；变化 region 比较原始 payload 后决定解码与复用；外置 chunk 按最终 inventory 关联。原始 region 所有字节仍可还原，opaque 不等于 Absent。

数据对象上限保持 64 MiB；新的 data pack 以 16 MiB 为目标并尽量不拆分可容纳的文件，metadata pack 上限 8 MiB，残余块按 64 KiB 对齐。旧 64 MiB pack 继续可读。checkpoint 只由 Host 明确请求，不再由周期、70% 变化或当前 root 放大比例自动触发。它搬运既有字节和索引，不重新捕获未变文件；已经自包含的新文件不重写第二套 recipe。保留放大统计计入 metadata pack 固定的额外数据依赖；当前计算逻辑字节，不能当作物理压缩率、全局可释放空间或 Cloud 存储量。

插件直接使用 Host 受控文件视图，不再复制 primary 到第二个 scratch。复用该稳定视图的文件摘要和本次 codec 的 payload 摘要；不从 timestamp 推断内容相同。顺序 materialization/repack 边读取边核验对象摘要，最后字节返回前必须通过；partial dispose 不认证内容，seek 仍走独立预验证。对象流池采用有界 LRU，避免达到 32 个闲置流后不断重开后续 pack。

`IncrementalTests` 包含 sparse、mcc 单改/删除/内外转换、orphan、opaque、header/padding/tail、显式 checkpoint、大比例变化保留未变引用、取消、非法变化集；`VerifiedObjectStreamTests` 覆盖摘要不符、截断、取消、seek、部分消费和失败后重读。测试不是性能 Gate。Host 的 P3 计划、格式合同和验收记录是集成状态来源；P1/P2 的历史验收继续有效。

默认一致性仍复制整个 Source，使用可取消的有界流式复制。没有宣称总 I/O 为 O(changes)，也没有宣称复制过程是 Minecraft 原子快照。正常入口需先通过 Host 规定的真实世界安全和性能 Gate。
