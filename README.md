# MineRewind - Minecraft 存档增强插件

为 [FolderRewind](https://github.com/Leafuke/FolderRewind) 提供 Minecraft 存档备份增强功能，重点覆盖热备份、热还原、自动发现世界存档，以及从现有配置识别 `.minecraft` 实例并创建配置。

MineRewind 1.9.0 起实现 FolderRewind 统一发现提供程序接口，最低需要 FolderRewind 1.9.0；旧的手动发现、批量创建和自动增强入口继续兼容。

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
- 自动扫描 `.minecraft/saves` 下的世界
- 支持 `.minecraft/versions/版本名/saves` 的版本隔离结构
- 自动识别 `.minecraft/mods` 和版本目录下的 `mods` 文件夹
- 自动读取世界根目录下的 `icon.png` 作为封面
- 在 FolderRewind“自动发现游戏存档 Beta”中按 Minecraft 实例返回独立候选配置，并以 `level.dat` 作为高可信证据

### 5. 自动识别并添加配置
- 可从现有 `Minecraft Saves` 配置的源路径定位 `.minecraft`
- 启用设置后立即扫描，并在每次 FolderRewind 启动时继续扫描
- 按实例根目录去重，为尚未管理的默认实例或版本隔离实例创建独立配置
- 混合配置引用到的每个实例都视为已管理，不会拆分或重复创建

### 6. 配置类型
- 定义 `Minecraft Saves` 配置类型
- 自动为每个实例创建独立配置，并直接使用实例名称作为配置名

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
| AutoDiscoverSaves | Boolean | true | 启动时向已有 Minecraft 实例配置补充新世界 |
| AutoCreateConfigs | Boolean | false | 从已有 Minecraft 配置定位 `.minecraft`，自动为未管理实例创建配置 |
| PreservePlayerData | Boolean | false | 普通还原时保留所有玩家的选定 NBT 字段；远程显式 true/false 可覆盖本次设置 |

## 热键

| 热键 | 作用 |
|------|------|
| Alt+Ctrl+S | 热备份当前正在运行的世界 |
| Alt+Ctrl+Z | 快速还原当前正在运行的世界 |

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
