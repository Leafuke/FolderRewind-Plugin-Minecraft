# MineRewind - Minecraft 存档增强插件

为 [FolderRewind](https://github.com/Leafuke/FolderRewind) 提供 Minecraft 存档备份增强功能，重点覆盖热备份、热还原和自动扫描 `.minecraft` 目录下的世界存档。

## 功能特性

### 1. 热备份 (Hot Backup)
- 自动检测世界目录是否被 `level.dat` 或 `session.lock` 占用
- 在检测到正在运行的世界时，和 KnotLink / MineBackup 侧进行协同保存
- 在无法建立联动时，仍可回退为普通备份流程

### 2. 热还原 (Hot Restore)
- 通过与 [MineBackup联动模组](https://github.com/Leafuke/MineBackup-Mod) 通信，实现 `Alt+Ctrl+Z` 快捷键以及 `/mb quickrestore` 指令还原。
- 支持自动退出存档、自动还原、自动重进。
- 支持多人联机环境下的热还原，确保所有玩家都能正确回到指定版本。

### 3. 批量扫描与配置创建
- 自动扫描 `.minecraft/saves` 下的世界
- 支持 `.minecraft/versions/版本名/saves` 的版本隔离结构
- 自动识别 `.minecraft/mods` 和版本目录下的 `mods` 文件夹
- 自动读取世界根目录下的 `icon.png` 作为封面

### 4. 配置类型
- 定义 `Minecraft Saves` 配置类型
- 自动为每个版本创建独立配置，配置名格式为 `Minecraft - 版本名`

### 5. KnotLink 扩展
- 使用严格键值对 v2：`cmd=BACKUP;current_save=true`、`cmd=LIST_BACKUPS;current_save=true`、`cmd=RESTORE;current_save=true[;file=...]`
- 当前世界备份支持一次性 `backup_mode`、`compression_method`、`compression_level` 参数；参数只影响本次归档，不写回 FolderRewind 配置
- 所有值按 RFC 3986 percent-encoding；省略 `file` 时还原最新备份，添加 `preserve_player_data=true` 时保留玩家数据
- 便于与 MineBackup 或其他支持 KnotLink 的组件联动

### 6. 指定区域备份

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
| EnableHotBackup | Boolean | true | 启用热备份功能 |
| PreservePlayerData | Boolean | false | 还原时保留玩家位置、物品栏、经验等数据 |

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
5. 如果需要热还原和保留玩家数据，请同时安装 MineBackup 联动模组以及 [KnotLink 服务端](https://github.com/KnotLink-Protocol/KnotLink/releases)。

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
