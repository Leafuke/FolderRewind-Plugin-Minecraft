# Minecraft World Incremental v1

实现为 `MineRewind.World/Semantic/WorldIncrementalFormat.cs` 和插件 `MinecraftIncrementalArtifactService`。当前编译契约为 Abstractions / API 3.18，持久化格式仍为 v1，程序集身份为 `3.0.0.0`。开发候选的本地验收不代表公开发布。

当前语义 Full、Smart 与 checkpoint 都使用本格式；旧 `minecraft-world-full` v1 的转换优化不得作为本格式的主流程收益。Host 提供 Full 或 ChangesOnly 的固定输入、基线身份、变化集和目录清单。插件只使用受控 Artifact 流，不持有 Host 应用或源码编译依赖，不回读实时世界。

完整目录引用 recipe/index/data packs，构成 Artifact DAG，不创建 Minecraft 私有版本链。未变文件不读源、不解码 chunk；变化 region 比较原始 payload 后决定解码与复用。外置 chunk 按最终 inventory 关联，所有原始 region 字节都必须可还原；opaque 不等于 Absent。

数据对象上限为 64 MiB；新 data pack 以 16 MiB 为目标并尽量不拆分可容纳的文件，metadata pack 上限 8 MiB，残余块按 64 KiB 对齐。旧 64 MiB pack 继续可读。checkpoint 只由 Host 明确请求，搬运既有字节和索引，不重新捕获未变文件，已经自包含的新文件不重写第二套 recipe。捕获不再生成引用区间计数或保留放大统计；recipe 摘要、依赖、引用范围、精确文件覆盖及 DAG 节点预算由显式验证承担。反序列化的旧状态首次使用时验证引用，进程内未变对象只保留不持久化的验证标记。

v1 的 `DeletedChunks` 字段暂保留为空，不扫描或生成删除说明字符串。有效删除输入、完整目录和 region 原始字节仍决定恢复结果。旧统计字段由 JSON reader 忽略，不参与完整性或删除授权。

顺序 materialization/repack 边读取边核验对象摘要，返回最后字节前必须通过。partial dispose 不认证内容；seek 走独立预验证。有界 LRU 限制空闲对象流数量。Host 执行 staging 封存、候选验证、封装、加密、History 提交及最终恢复事务。空目录清单由 Host 管理。

DirectOnly、AlwaysCopy、Automatic 源读取选择由 Host 决定，插件优化不得暗改模式。读取保护和源变化检查不是 Minecraft 原子快照；强一致性与降级规则仍由既有协调策略决定。持久化世界身份、region 地址、指纹及合并策略版本保持不变。

字节摘要不符、截断、路径越界和源 I/O 失败不得变成“语义覆盖不足”的成功结果。缺少语义支持的合法原始字节可以被完整保留并返回覆盖诊断。预算和可选状态摘要见 [read and state v1](../../contracts/minecraft-read-and-state-v1.md)。
