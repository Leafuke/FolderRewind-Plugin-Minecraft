# Minecraft World Full v1

状态：P2 开发候选。公共 Plugin API 仍为 3.9；不安装、不发布、不承诺 P1 probe scratch 的兼容性。

## 身份与适用边界

- Transformer：`com.folderrewind.minerewind/minecraft-world-full`。
- Artifact format：同名，版本 `1`；strategy：`minecraft-world-materializer`。
- 只接受 Java Config Kind、Full、Complete。Full 指 Host 的 Effective Source Boundary。
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

输入是 Host 已验证 CoreFull 的只读文件视图。插件不重读 live world。
通过受限 handles 流式复制到自己的 seekable 临时存储，逐文件核对长度与 SHA-256。
复用 P1 codec/index，无重新压缩或 NBT 重新序列化。完成独立格式验证后才写 Host staging。
捕获前保守估算文件数与索引开销；沿用 100,000 文件 / 16 GiB staging 上限及 P1 16 MiB 单 payload 解码上限。
最终 Host staging 和 restore workspace 自身的限额仍适用；没有放宽现有预算。

Materializer 只读受控 Artifact 视图，只写 `IRestoreMaterializationWorkspace`。
不接触密码、History、备份仓库写路径、live mutation continuation。
gzip/zlib/raw、external `.mcc`、非 region 文件按 P1 的无损规则处理。
未知压缩、零字节或损坏 region 可以 opaque 保留；缺失外部文件不会凭空生成。

## 保真与覆盖是两种结论

Host independently 比较 CoreFull 与 materialized 文件集合、长度和 SHA-256 后才登记 Exact。
`minerewind.full.semantic_coverage_limited` 记录 opaque 计数，不将字节 Exact 降为 Partial。
Exact 仅说明捕获边界的文件字节可重建，不意味着世界结构健康或每个 chunk 都可语义比较。
读失败、输入 hash 不符、配额超限、重建差异必须失败，不跳过文件。

Host 封存、加密、双表示提交、Retention、Cloud 均由 Host 实现。
本格式不是 chunk delta，不宣传减少存储；不实现语义 Merge、地图选择或 Capture API。
