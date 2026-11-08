# Minecraft World Full v1

状态：仅供读取兼容的历史 Full 格式，已停止生成。当前源码要求 Plugin API / Abstractions 3.18；是否公开发布由精确 SDK 来源记录决定，本文不宣告发布。probe scratch 不是持久化格式。

## 身份与适用边界

- Transformer 身份 `com.folderrewind.minerewind/minecraft-world-full` 继续用于当前增量链路，不再生成本格式。
- Artifact format：同名，版本 `1`；strategy：`minecraft-world-materializer`。
- 本格式历史产物对应 Java Config Kind、Full、Complete；Full 指当时 Host 的 Effective Source Boundary。
- 一个 root Artifact 自包含所有文件，不引用 Core primary 或旧 Version。
- SourceId 使用请求 FolderId 的规范 GUID 表示；world-relative identity、dimension、component 和 chunk 坐标继续使用 P1 模型。

## 逻辑格式

`world-full.json` 是长期读取入口，UTF-8、属性名区分大小写，v1 使用 PascalCase：

| 字段 | 含义 |
|---|---|
| Format / Version | 完整 format ID / `1` |
| SourceId | 来源身份，不是磁盘路径 |
| FilesHash / RegionsHash / DiagnosticsHash | 对应 JSONL 文件的原始字节 SHA-256 |
| Files / Bytes | 文件数量与重建原始字节总数 |
| Regions / OpaqueRegions | region 总数与 opaque 数量 |
| MissingExternalFiles / UnclassifiedRegions | 覆盖诊断计数 |

`files.jsonl` 按相对路径序排列，每行包含 `RelativePath`, `Length`, `Sha256`, `Encoding`, `Segments`。
每个 segment 固定为 `FileOffset`, `Length`, `ObjectPath`, `ObjectOffset`, `Sha256`。
`Encoding` 的 v1 字符串为 `Raw`, `SegmentedRegion`, `OpaqueRegion`；范围覆盖无遗漏、无重叠。
对象位于 `objects/`，保存原压缩 payload、容器 residual 或完整 opaque/raw 字节。
`regions.jsonl` 保存 P1 region/component/chunk 索引；`diagnostics.jsonl` 保存领域诊断。

这些 v1 字段与 P1 当前底层 DTO 共用实现，但本文件独立定义它们的读取契约。
今后调整实验 scratch 时，必须保留 v1 reader 或增加格式版本。独立手写 v1 fixture 验证读取兼容性。
不导出 `complete.json`、probe 计时/临时目录，也不导出重复的逐 region 中间 manifest。

## 读写、安全和资源

缺少 `IncrementalInput` 的转换请求返回 `Blocked`、空 Patch 和带中英文说明的 `minerewind.incremental_input_required` 错误诊断。拒绝发生在插件创建临时目录、读取输入或分配 staging 之前；不根据参数默认值推测或补造捕获上下文。
当前语义 Full、Smart、checkpoint 均生成 `minecraft-world-incremental` v1。旧写入器、导出入口及其 scratch 优化已移除；离线 WorldProbe 的严格捕获及源变化复查继续保留。

旧格式 materializer 继续检查 SourceId、文件清单、实际长度、对象摘要及区段覆盖。输入副本沿用 100,000 文件 / 16 GiB 上限，重建输出遵守 Host workspace 的限额。
materialization 输入写入前经 `TemporaryStorage.ReserveAsync` 预留尚待写入的字节。写入完成即释放预留，存活文件由实际卷可用空间计入；清理失败附加到原异常，不掩盖失败原因。

Materializer 只读受控 Artifact 视图，只写 `IRestoreMaterializationWorkspace`。
不接触密码、History、备份仓库写路径、live mutation continuation。
gzip/zlib/raw、external `.mcc`、非 region 文件按 P1 的无损规则处理。
未知压缩、零字节或损坏 region 可以 opaque 保留；缺失外部文件不会凭空生成。

## 保真与覆盖是两种结论

历史 Exact 表示捕获边界内的文件字节可完整重建。现有读取继续核对文件集合、长度和 SHA-256。
Exact 仅说明捕获边界的文件字节可重建，不意味着世界结构健康或每个 chunk 都可语义比较。
读失败、输入 hash 不符、配额超限、重建差异必须失败，不跳过文件。

Host 封存、加密、History、Retention、Cloud 均由 Host 实现。保留本读取器不扩展当前 Host 对旧实验 History 的支持；Host 的新版捕获证据要求保持不变，不自动迁移已有历史。
本格式不是 chunk delta。当前主流程见 [Minecraft World Incremental v1](minecraft-world-incremental-v1.md)；布局事实及用途策略见 [world-layout-facts](world-layout-facts.md)。
