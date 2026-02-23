# MineRewind - Minecraft 存档增强插件

为 [FolderRewind](https://github.com/Leafuke/FolderRewind) 提供 Minecraft 存档备份增强功能。

## 功能特性

### 1. 热备份 (Hot Backup)
- 使用 `xcopy` 命令创建存档快照，避免游戏运行时文件被占用导致备份失败
- 自动检测 `level.dat` 文件锁定状态
- 备份完成后自动清理临时快照

### 2. 热还原 (Hot Backup)
- 通过与 [MineBackup联动模组](https://github.com/Leafuke/MineBackup-Mod) 通信，实现 `Alt+Ctrl+Z` 快捷键还原或者游戏内指令还原。
- 支持自动退出存档、自动还原、自动重进。

### 3. 批量扫描与配置创建
- 自动发现 `.minecraft` 目录下的所有存档以及 `mods` 文件夹
- 支持多版本隔离模式 (`.minecraft/versions/版本名/saves`)
- 为每个游戏版本自动创建独立的备份配置

### 4. 配置类型
- 定义 `Minecraft Saves` 配置类型
- 支持存档封面图片 (`icon.png`) 显示

## 插件设置

| 设置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| EnableHotBackup | Boolean | true | 启用热备份功能 |
| SnapshotPath | Path | (系统临时目录) | 快照存储路径 |
| CleanupSnapshot | Boolean | true | 备份后自动清理快照 |
| SnapshotDelayMs | Integer | 500 | 创建快照后的等待时间(毫秒) |

## 目录结构识别

插件支持以下目录结构：

### 标准模式
```
.minecraft/
└── saves/
    ├── World1/
    │   └── level.dat
    └── World2/
        └── level.dat
```

### 版本隔离模式 (HMCL/PCL2 等启动器)
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

## 使用方法

1. 安装插件到 FolderRewind （可以下载 [Releases](https://github.com/Leafuke/FolderRewind-Plugin-Minecraft/releases) 中的 `.zip` 文件后，使用“本地安装”功能导入；或者在插件市场中搜索安装）
2. 在设置中启用插件
3. 在"新建配置"时选择扫描 `.minecraft` 目录
4. 插件会自动为每个游戏版本创建备份配置
5. 如果你希望与 MineBackup联动模组 进行联动，务必安装联动模组以及[KnotLink服务端](https://github.com/hxh230802/KnotLink/releases)

- FolderRewind 下载：

<a href="https://apps.microsoft.com/detail/9nwsdgxdqws4?referrer=appbadge&mode=direct">
	<img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/>
</a>

## 构建说明

1. 使用 Visual Studio 2026 
2. 确保已安装 .NET 10.0 SDK
3. 打开解决方案文件或直接构建此项目
4. 构建输出在 `bin/Release/net10.0-windows10.0.19041.0/` 目录

### 打包为插件

1. 构建 Release 版本
2. 将以下文件打包为 `.zip`:
   - `MineRewind.dll`
   - `manifest.json`
