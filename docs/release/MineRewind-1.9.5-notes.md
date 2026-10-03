# MineRewind 1.9.5

## 中文

适用于支持 Plugin API 3.6 的 FolderRewind 1.9.3；构建 SDK 为 FolderRewind.Plugin.Abstractions 3.6.0。

- 切换到 Plugin API 3 公开契约和 `.frplugin` 包格式；不携带宿主的 Abstractions DLL。
- 扩展 Minecraft Java/基岩版存档及多启动器发现，支持已知位置和用户指定根目录，并允许 Minecraft 配置包含普通文件夹。
- 提供 Minecraft 区域备份范围、一致性协调、版本元数据和宿主管理的还原准备。
- 完善玩家数据保留、当前世界目标解析、快速恢复及 KnotLink 命令能力声明与协调。

下载 `MineRewind-1.9.5.frplugin` 并核对 SHA-256，可通过插件商店或本地插件安装入口安装。旧插件使用新的公开 API，需要兼容的宿主版本。Minecraft Merge 仍使用宿主的通用保守文件级合并，不注册 region/chunk/NBT 语义合并。

## English

Requires a Host supporting Plugin API 3.6, such as FolderRewind 1.9.3. The build SDK is FolderRewind.Plugin.Abstractions 3.6.0.

- Uses public Plugin API 3 contracts and `.frplugin` packaging without bundling the Host Abstractions DLL.
- Extends Java/Bedrock and launcher discovery, supports known locations and explicit roots, and allows ordinary folders in Minecraft configurations.
- Provides region backup scopes, consistency coordination, version metadata and Host-managed restore preparation.
- Improves player preservation, current-world resolution, quick restore and KnotLink command coordination.

Install `MineRewind-1.9.5.frplugin` through the plugin store or local package installation after verifying SHA-256. A compatible Host is required. Minecraft Merge remains a conservative file-level Host operation, without region/chunk/NBT semantic merging.
