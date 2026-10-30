# 世界预览改进记录

日期：2026-10-22。此文档随源码提交；本轮遵照用户要求，不运行自动化测试、UI 自动化或性能基准。版本号为本地开发产物，未发布。

## 交付

- 1.9.11：保留原有正式版本，补齐 26.2（4903）、26.3（5023）。按每个区块的 DataVersion 读取；新 palette 支持 id/properties、字符串简写及空名称包装。未知版本仍标为不支持，不修改 Native 语义规范化规则。
- 1.9.11：Java LZ4Block 内嵌与外置 mcc，使用 K4os 原始块解码和 .NET XXH32；检查魔数、方法、长度、终止块、校验和与解压额度，损坏按区块隔离。
- 1.9.12：地表、从 Y 向下寻找空腔地面、精确切片。每个视图/高度分开缓存；瓦片与点击详情共用参数；玩家位置提供保存 Y。
- 1.9.12：可选状态叠加、图例、单位、无记录/无表面/未知内容状态、已加载区域统计。远景只读取区域头，不将索引占用算作已验证。
- 1.9.13：部分历史目录专用识别，不放宽备份/还原的 WorldLocator；缺少 level.dat 可从 region/DIM/dimensions 结构识别。明确的 server.properties level-name 优先；不能识别或多个候选时提供中英文原因。部分空白始终使用“未包含或未生成”。
- 1.9.13：模式可分别声明主动导航高度与默认导航高度；下界默认洞穴 Y=120，主动定位玩家使用保存 Y，普通洞穴首次可使用保存 Y。

## 实际验证

各阶段独立 Release build、PackFrPlugin 通过，0 警告、0 错误。精确冻结 API 3.11 契约包经过包摘要和 DLL 摘要核验，并由 Host 集成脚本逐文件核对来源。正式包只包含两份业务 DLL、fNbt、K4os、System.IO.Hashing、依赖许可、manifest、schema 与契约来源；不包含 Host Abstractions DLL。

尚未进行实际游戏世界的渲染或人工 UI 验收。请在 Host 的 `docs/plans/World_Preview_Improvement_Plan.md` 按完整清单验收旧版本、高版本、洞穴、下界、负 Y、无表面、未知/损坏/缺色、普通/增量/语义/加密/部分历史以及取消和清理。

## 参考与许可

- [Minecraft Java Edition 26.3 官方说明](https://www.minecraft.net/en-us/article/minecraft-java-edition-26-3)。正式 DataVersion 表参考 [mcmeta summary](https://github.com/misode/mcmeta/tree/summary/versions)。
- `Reference/mcaselector-master` 的版本配色与渲染做法；具体原文件身份、SHA256、阈值和 MIT 许可见 `MineRewind.World/Colors/README.md`。没有打包 Minecraft JAR 或原始纹理。
- [lz4-java 的 LZ4BlockInputStream](https://github.com/lz4/lz4-java/blob/master/src/java/net/jpountz/lz4/LZ4BlockInputStream.java)；新增解码依赖的完整许可在 `MineRewind/preview-dependencies.NOTICE.txt`，随包分发。
