# MineRewind - Minecraft 存档增强插件

为 [FolderRewind](https://github.com/Leafuke/FolderRewind) 提供 Minecraft 存档备份增强功能，重点覆盖热备份、热还原、自动发现世界存档，以及从现有配置识别 `.minecraft` 实例并创建配置。

MineRewind 1.9.0 起实现 FolderRewind 统一发现提供程序接口。当前 1.9.5 要求支持 Plugin API 3.6 的 Host（FolderRewind 1.9.3），构建依赖 NuGet 包 FolderRewind.Plugin.Abstractions 3.6.0。旧的手动发现与批量创建入口继续兼容。正式版本以 GitHub Release 和 nuget.org 的公开记录为准。

Java 的热备份、热还原、NBT 玩家保留和区域备份只适用于 `Minecraft Saves`。新增 `Minecraft Bedrock Saves` 使用普通目录备份与还原，请先关闭游戏。

## 功能特性

### 1. 热备份 (Hot Backup)
- 自动检测世界目录是否被 `level.dat` 或 `session.lock` 占用
- 在检测到正在运行的世界时，和 KnotLink / MineBackup 侧进行协同保存
- 在无法建立联动时，仍可回退为普通备份流程

### 2. 热还原 (Hot Restore)
- 通过与 [MineBackup联动模组](https://github.com/Leafuke/MineBackup-Mod) 通信，实现 `Alt+Ctrl+Z` 快捷键以及 `/mb quickrestore` 指令还原。
- 历史页还原正在运行的世界时，会与热键共用 `handshake → pre_hot_restore → 世界释放 → RESTORE → rejoin` 流程，不需要单独的游戏内倒计时请求。
- 完整备份热还原使用清理模式，部分备份热还原使用覆写模式。
- 保留玩家数据通过 Host 管理的只读 current/target 视图生成 staging proposal，并纳入同一次还原事务；插件不会在 Host continuation 返回后写回正在管理的世界目录。
- 支持自动退出存档、自动还原、自动重进。
- 支持多人联机环境下的热还原，确保所有玩家都能正确回到指定版本。

### 3. 分支合并边界

- MineRewind 参与配置级 Restore/Checkout/Merge 环境协调，并检查全部受影响世界。
- FolderRewind 1.9.0 对 Minecraft 存档仍使用通用保守文件级三方合并；插件不注册 region/chunk/NBT 语义 Merge provider。
- `.mca`、玩家 NBT、stats 或 advancements 双方都改变时按文件冲突处理，不显示区块级自动合并。

### 4. 批量扫描与配置创建
- 支持官方 Java 默认位置、HMCL、PCL2、PCLCE、Prism、Modrinth、网易中国版及 Bedrock 的常见位置
- 自动扫描 `.minecraft/saves` 下的世界
- 支持 `.minecraft/versions/版本名/saves` 的版本隔离结构
- 自动识别 `.minecraft/mods` 和版本目录下的 `mods` 文件夹
- 自动读取世界根目录下的 `icon.png` 作为封面
- 在 FolderRewind“自动发现游戏存档 Beta”中按 Java 实例或 Bedrock 世界集合返回候选配置；`level.dat` 存在即候选，不检查世界内容是否有效
- 手选启动器、游戏根、实例库、saves、minecraftWorlds 或单世界；完成扫描后 Host 记住选定根，手选扫描不会混入全局已知位置
- 单轮最多 500 个实例，达到扫描预算时保留已完成结果；配置文件读取最多 1 MiB

### 5. 自动识别并添加配置
- 自动发现合并已知位置、记住的目录及现有 Minecraft 配置附近目录
- `AutoDiscoverSaves` 控制发现提供程序；开启 `AutoCreateConfigs` 时 Host 在启动后执行扫描并提交候选
- 默认仅生成可审阅的草稿；只有开启 `AutoCreateConfigs` 后才在启动时提交候选
- 按实际路径去重，已管理的源不会再次加入新配置，已有混合配置不会被拆分

### 6. 配置类型
- 定义 `Minecraft Saves` 配置类型
- 定义 `Minecraft Bedrock Saves`，不注册 Java 专属能力；普通文件流程会保留整个世界目录，包括 `db`
- 自动为每个实例创建独立配置，并直接使用实例名称作为配置名

### 启动器发现边界（1.9.5）

| 来源 | 简单定位方法 | 手选兜底范围 |
| --- | --- | --- |
| 官方 Java | `%APPDATA%/.minecraft` | 自定义 gameDir |
| HMCL | 全局目录登记、新旧工作区路径字段 | 未定位的便携工作区或未知相对根 |
| PCL2 | 指定注册表的 LaunchFolders（缺失正常）和 CacheDownloadFolder；缓存向上最多三层验证本地配置，再读 Select | 没有定位线索的启动器或游戏根 |
| PCLCE | `%APPDATA%/PCLCE/config.v1.json` 与两个旧 JSON 位置；读取 LaunchFolders/CacheDownloadFolder | 非已知共享位置；不解析本地 YAML |
| Prism | AppData/Scoop、InstanceDir、instance.cfg；minecraft 优先回退 .minecraft | 便携数据根或实例库 |
| Modrinth | 新旧 AppData 的 profiles、可见 THESEUS_CONFIG_DIR | 数据库中的自定义存储根 |
| 网易 | DownloadPath 的 Game/.minecraft 与已知网易 minecraftWorlds | 其他本地布局 |
| Bedrock | 新版 Users 世界集合和旧 UWP 集合 | Preview/特殊发行版的其他位置 |

PCL 下载缓存只是定位线索，不保证属于当前安装。仅确认目录标记后才展开 `$.minecraft` 等 Select 值；不固定裁剪目录层级、不要求启动器运行、不读取账号字段。不同路径的迁移残留可以同时列出，按路径去重即可。发现不启动自动备份、移动世界或判断存档健康。

### 7. KnotLink 扩展（MineRewind 1.9.3 / Plugin API 3.5）

- 严格 v2 键值对及 RFC 3986 percent-encoding。`BACKUP`、`LIST_BACKUPS`、`RESTORE`、`AUTO_BACKUP`、`STOP_AUTO_BACKUP`、`MARK_IMPORTANT` 可用 `current_save=true` 选择唯一运行世界，不能同时传 `config_id/folder`；无世界、多个活动世界或插件不可用时返回错误。
- 当前世界选择只解析稳定目标，操作由 FolderRewind 共用处理器执行。备份支持备注、`backup_mode`、`compression_method`、`compression_level`、黑白名单及 `backup_scope` / `scope_*`；自动备份固定启动时的世界和选项。
- 所有有副作用的命令必须提供 `from` 和 `request_id`，例如 `cmd=RESTORE;current_save=true;preserve_player_data=false;from=my.client;request_id=restore-001`。
- 两种远程 `RESTORE` 默认均为 `clean`；显式 `mode=overwrite` 可覆盖，部分备份始终使用覆盖还原。两种路径省略 `file` 时均恢复活动 Workspace 的唯一局部分支尖端，不按归档时间选择其他分支。
- `preserve_player_data` 缺省继承插件本地设置，显式 true/false 覆盖本次操作。标量选项只影响本次操作，黑白名单列表追加去重，不写回本地配置。
- 开启保留时，保留全部玩家（包括离线玩家）的现有位置、朝向、维度、背包、末影箱、经验、分数、游戏模式、生命值、饥饿及饱和度字段。目标备份没有的 UUID 保留完整当前 NBT；其他字段、进度与统计正常回档。
- 保留适配旧版 `level.dat/Data/Player` 和 `playerdata`，以及 26.1 的 `singleplayer_uuid` 和 `players/data`；开启保留时禁止跨布局还原。损坏、身份冲突、越界或超限阻止整次还原，不返回部分玩家结果。上限 4,096 个提案、64 MiB；`.dat_old` 不作为玩家主体或自动回退源。
- `GET_CAPABILITIES` 发布六个独立名称的当前世界能力、四个模组回执和协作事件；如 `minerewind_restore_current_save` 仍发送 `cmd=RESTORE;current_save=true`。
- 上述存档结构已按官方资料及生成 NBT 测试验证，真实 1.21.11 / 26.1 单人和多人游戏加载验收仍待完成。

### 8. 指定区域备份

- 每行区域必须使用 `x1,z1,x2,z2`，坐标按不变量格式解析，范围为 `[-30000000, 30000000]`
- 最多接受 32 KiB、128 个非空非注释行，以及每个维度 4096 个去重后的区域文件
- 可选择主世界、下界和末地；任一非法区域、非法维度开关或缺失维度都会终止整个备份
- 每个区域会同时包含 `region`、`entities`、`poi` 中对应的 `.mca`，并包含所选维度这三个目录下的外部区块 `.mcc`
- 区域范围会替换配置中的普通备份白名单，避免手工规则意外扩大备份范围
- 区域备份属于部分备份；热还原和普通还原都只使用覆写模式，不会先清空目标目录

支持的维度布局：

- Minecraft 26.1：`dimensions/minecraft/overworld`、`the_nether`、`the_end`
- 旧版原版：世界根目录、`DIM-1`、`DIM1`
- Paper/Spigot：主世界目录，以及同级的 `<world>_nether/DIM-1`、`<world>_the_end/DIM1`

选择服务器根目录时，插件通过 `server.properties` 中的 `level-name` 定位主世界。Paper/Spigot 的下界或末地位于主世界同级目录，因此要备份这些维度必须把服务器根目录选为备份源；只选择主世界目录会被安全校验拒绝。新旧布局混用、同一维度多重命中或所选维度不在备份源内部时同样会拒绝备份。

## 插件设置

| 设置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| AutoDiscoverSaves | Boolean | true | 允许发现本机世界并生成可审阅的配置草稿 |
| AutoCreateConfigs | Boolean | false | 启动时扫描已知位置、记住的根及已有配置附近目录，经 Host 校验后为未管理世界创建配置 |
| PreservePlayerData | Boolean | false | Java 普通还原时保留所有玩家的选定 NBT 字段；远程显式 true/false 可覆盖本次设置 |

## 热键

| 热键 | 作用 |
|------|------|
| Alt+Ctrl+S | 热备份当前正在运行的 Java 世界 |
| Alt+Ctrl+Z | 快速还原当前正在运行的 Java 世界 |

## 目录结构识别

插件支持以下目录结构：

### 标准模式
```
.minecraft/
└── saves/
    ├── World1/
    │   ├── level.dat
    │   └── icon.png
    └── World2/
        └── level.dat
```

### 版本隔离模式 (HMCL / PCL2 等启动器)
```
.minecraft/
└── versions/
    ├── 1.20.1/
    │   └── saves/
    │       └── World1/
    │           └── level.dat
    └── 1.21/
        └── saves/
            └── World2/
                └── level.dat
```

### 直接选择存档目录
```
World1/
└── level.dat
```

## 使用方法

1. 安装插件到 FolderRewind。可以下载 [Releases](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) 中的 `.zip` 文件后，使用“本地安装”导入；也可以在插件市场中搜索安装。
2. 在设置中启用插件。
3. 新建配置时选择扫描 `.minecraft` 目录，或者直接选择 `saves` / 单个世界目录。
4. 插件会自动按 Minecraft 版本创建备份配置。
5. 如果需要热还原和保留玩家数据，请同时安装 MineBackup 联动模组以及 [KnotLink 服务端](https://github.com/KnotLink-Protocol/KnotLinkService/releases)。

- FolderRewind 下载：

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

## 构建说明

1. 使用 Visual Studio 2026
2. 确保已安装 .NET 10.0 SDK
3. 打开解决方案文件或直接构建 `MineRewind/MineRewind.csproj`
4. 目标框架为 `net10.0-windows10.0.19041.0`
5. 构建输出在 `MineRewind/bin/Release/net10.0-windows10.0.19041.0/` 目录

### 打包为插件

1. 构建 Release 版本
2. 将以下文件打包为 `.zip`：
   - `MineRewind.dll`
   - `manifest.json`
   - `fNBT.dll`
