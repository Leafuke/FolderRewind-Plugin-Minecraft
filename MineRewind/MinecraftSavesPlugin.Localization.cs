using System.Globalization;
using Windows.Globalization;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        private static readonly IReadOnlyDictionary<string, string> EnUsTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MineRewind_Setting_AutoDiscoverSaves_Name"] = "Auto-discover saves",
            ["MineRewind_Setting_AutoDiscoverSaves_Desc"] = "When FolderRewind starts, automatically add newly created worlds from the sibling saves directories of existing Minecraft Saves entries.",
            ["MineRewind_Setting_PreservePlayerData_Name"] = "Preserve player data on restore",
            ["MineRewind_Setting_PreservePlayerData_Desc"] = "Keep the current player's position, inventory, XP, etc. when restoring a save (single-player only). Usually not needed.",
            ["MineRewind_BackupScope_SelectedRegions_Name"] = "Minecraft Selected Regions",
            ["MineRewind_BackupScope_SelectedRegions_Desc"] = "Back up selected block-coordinate rectangles plus essential save files. The plugin detects Minecraft 26.1+, legacy Vanilla, and Paper/Spigot dimension layouts from the actual source directories.",
            ["MineRewind_BackupScope_DimensionOverworld_Name"] = "Overworld",
            ["MineRewind_BackupScope_DimensionOverworld_Desc"] = "Uses dimensions/minecraft/overworld in Minecraft 26.1+ saves, or the save root in older saves.",
            ["MineRewind_BackupScope_DimensionNether_Name"] = "Nether",
            ["MineRewind_BackupScope_DimensionEnd_Name"] = "The End",
            ["MineRewind_BackupScope_Areas_Name"] = "Selected areas",
            ["MineRewind_BackupScope_Areas_Desc"] = "One rectangle per line: x1,z1,x2,z2. Use finite block X/Z coordinates between -30,000,000 and 30,000,000; at most 4096 region files per dimension.",
            ["MineRewind_BackupScope_Error_Context"] = "The selected-region backup context is invalid.",
            ["MineRewind_BackupScope_Error_WorldNotFound"] = "No Minecraft world could be resolved from source folder: {0}",
            ["MineRewind_BackupScope_Error_WorldOutsideSource"] = "The resolved Minecraft world is outside the configured backup source.",
            ["MineRewind_BackupScope_Error_InputTooLarge"] = "Selected-region input exceeds the {0}-byte limit.",
            ["MineRewind_BackupScope_Error_AreaRequired"] = "At least one selected-region rectangle is required.",
            ["MineRewind_BackupScope_Error_TooManyLines"] = "Selected-region input may contain at most {0} non-comment lines.",
            ["MineRewind_BackupScope_Error_InvalidArea"] = "Selected-region line {0} is invalid: {1}",
            ["MineRewind_BackupScope_Error_RegionLimit"] = "Selected regions exceed the limit of {0} distinct region coordinates per dimension.",
            ["MineRewind_BackupScope_Error_InvalidDimension"] = "Unknown Minecraft dimension: {0}",
            ["MineRewind_BackupScope_Error_InvalidDimensionFlag"] = "A selected-region dimension flag is invalid.",
            ["MineRewind_BackupScope_Error_DimensionRequired"] = "Select at least one Minecraft dimension.",
            ["MineRewind_BackupScope_Error_DimensionOutsideSource"] = "The {0} directory is outside the configured source. For Paper/Spigot servers, select the server root.",
            ["MineRewind_BackupScope_Error_DimensionMissing"] = "The selected {0} directory does not exist.",
            ["MineRewind_BackupScope_Error_DimensionAmbiguous"] = "Multiple layouts were found for {0}. Remove migration leftovers or select an unambiguous source.",
            ["MineRewind_BackupScope_Error_DimensionMixed"] = "Selected dimensions mix Minecraft 26.1+ and legacy directory layouts.",
            ["MineRewind_CreateConfigs_Result"] = "Created {0} Minecraft saves configs",
            ["MineRewind_Hotkey_ActiveWorldBackup_Name"] = "Hot backup the active world",
            ["MineRewind_Hotkey_ActiveWorldBackup_Desc"] = "Detect the running Minecraft world (locked files) and trigger a hot backup.",
            ["MineRewind_Hotkey_QuickRestore_Name"] = "Quick restore current save",
            ["MineRewind_Hotkey_QuickRestore_Desc"] = "Restore the currently running Minecraft save to the latest backup (requires companion mod)",
            ["MineRewind_Hotkey_NoActiveWorld"] = "[MineRewind] No active world detected (no locked save found).",
            ["MineRewind_Hotkey_Failed"] = "[MineRewind] Hotkey-triggered backup failed: {0}",
            ["MineRewind_Hotkey_QuickRestore_NoActive"] = "[MineRewind] Quick restore: no active save detected",
            ["MineRewind_Hotkey_QuickRestore_Failed"] = "[MineRewind] Quick restore hotkey failed: {0}",
            ["MineRewind_KnotLink_BackupCurrent_Failed"] = "[MineRewind] KnotLink BACKUP current_save failed: {0}",
            ["MineRewind_Details_SectionTitle"] = "Minecraft world info",
            ["MineRewind_Details_WorldName"] = "World name",
            ["MineRewind_Details_GameMode"] = "Game mode",
            ["MineRewind_Details_Seed"] = "Seed",
            ["MineRewind_Details_WorldDays"] = "World days",
            ["MineRewind_Details_TotalTime"] = "World total time",
            ["MineRewind_Details_LastPlayed"] = "Last played",
            ["MineRewind_Details_PlayerData"] = "Player data",
            ["MineRewind_Details_Yes"] = "Yes",
            ["MineRewind_Details_No"] = "No",
            ["MineRewind_Details_Format"] = "Format",
            ["MineRewind_Details_Format_New"] = "Minecraft 26.1+",
            ["MineRewind_Details_Format_Legacy"] = "Legacy (< 26.1)",
        };

        private static readonly IReadOnlyDictionary<string, string> ZhCnTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MineRewind_Setting_AutoDiscoverSaves_Name"] = "自动发现存档",
            ["MineRewind_Setting_AutoDiscoverSaves_Desc"] = "FolderRewind 启动时，自动从已有 Minecraft 存档所在的同级 saves 目录补充新建世界。",
            ["MineRewind_Setting_PreservePlayerData_Name"] = "还原时保留玩家数据",
            ["MineRewind_Setting_PreservePlayerData_Desc"] = "还原存档时**不还原**当前玩家的位置、物品栏、经验等数据（仅单人存档）。一般不需要开启。",
            ["MineRewind_BackupScope_SelectedRegions_Name"] = "Minecraft 指定区域",
            ["MineRewind_BackupScope_SelectedRegions_Desc"] = "按方块 X/Z 坐标备份所选矩形区域及存档基础文件。插件会根据实际源目录识别 Minecraft 26.1+、旧版原版和 Paper/Spigot 维度布局。",
            ["MineRewind_BackupScope_DimensionOverworld_Name"] = "主世界",
            ["MineRewind_BackupScope_DimensionOverworld_Desc"] = "26.1+ 存档使用 dimensions/minecraft/overworld，旧版存档使用存档根目录。",
            ["MineRewind_BackupScope_DimensionNether_Name"] = "下界",
            ["MineRewind_BackupScope_DimensionEnd_Name"] = "末地",
            ["MineRewind_BackupScope_Areas_Name"] = "指定区域坐标",
            ["MineRewind_BackupScope_Areas_Desc"] = "每行一个矩形：x1,z1,x2,z2。请填写 -30,000,000 至 30,000,000 之间的有限方块 X/Z 坐标；每个维度最多 4096 个区域文件。",
            ["MineRewind_BackupScope_Error_Context"] = "指定区域备份上下文无效。",
            ["MineRewind_BackupScope_Error_WorldNotFound"] = "无法从以下源文件夹识别 Minecraft 世界：{0}",
            ["MineRewind_BackupScope_Error_WorldOutsideSource"] = "识别到的 Minecraft 世界位于配置的备份源之外。",
            ["MineRewind_BackupScope_Error_InputTooLarge"] = "指定区域输入超过 {0} 字节上限。",
            ["MineRewind_BackupScope_Error_AreaRequired"] = "至少需要填写一个指定区域矩形。",
            ["MineRewind_BackupScope_Error_TooManyLines"] = "指定区域最多允许 {0} 个非注释行。",
            ["MineRewind_BackupScope_Error_InvalidArea"] = "指定区域第 {0} 行无效：{1}",
            ["MineRewind_BackupScope_Error_RegionLimit"] = "指定区域超过每个维度 {0} 个去重区域坐标的上限。",
            ["MineRewind_BackupScope_Error_InvalidDimension"] = "未知的 Minecraft 维度：{0}",
            ["MineRewind_BackupScope_Error_InvalidDimensionFlag"] = "指定区域的维度开关值无效。",
            ["MineRewind_BackupScope_Error_DimensionRequired"] = "请至少选择一个 Minecraft 维度。",
            ["MineRewind_BackupScope_Error_DimensionOutsideSource"] = "{0}目录位于配置源之外。Paper/Spigot 服务器请改选服务器根目录。",
            ["MineRewind_BackupScope_Error_DimensionMissing"] = "所选{0}目录不存在。",
            ["MineRewind_BackupScope_Error_DimensionAmbiguous"] = "{0}同时存在多种布局。请清理迁移残留或选择无歧义的备份源。",
            ["MineRewind_BackupScope_Error_DimensionMixed"] = "所选维度混用了 Minecraft 26.1+ 与旧版目录布局。",
            ["MineRewind_CreateConfigs_Result"] = "已创建 {0} 个 Minecraft 存档配置",
            ["MineRewind_Hotkey_ActiveWorldBackup_Name"] = "热备份正在运行的世界",
            ["MineRewind_Hotkey_ActiveWorldBackup_Desc"] = "检测正在运行（文件被占用）的 Minecraft 存档，并触发热备份。",
            ["MineRewind_Hotkey_QuickRestore_Name"] = "快速还原当前存档",
            ["MineRewind_Hotkey_QuickRestore_Desc"] = "将当前正在运行的 Minecraft 存档还原到最新备份（需要联动模组支持）",
            ["MineRewind_Hotkey_NoActiveWorld"] = "[MineRewind] 未检测到正在运行的世界（未发现被占用的存档）。",
            ["MineRewind_Hotkey_Failed"] = "[MineRewind] 热键触发备份失败：{0}",
            ["MineRewind_Hotkey_QuickRestore_NoActive"] = "[MineRewind] 快速还原：未检测到活跃存档",
            ["MineRewind_Hotkey_QuickRestore_Failed"] = "[MineRewind] 快速还原热键失败：{0}",
            ["MineRewind_KnotLink_BackupCurrent_Failed"] = "[MineRewind] KnotLink BACKUP current_save 失败：{0}",
            ["MineRewind_Details_SectionTitle"] = "Minecraft 世界信息",
            ["MineRewind_Details_WorldName"] = "世界名称",
            ["MineRewind_Details_GameMode"] = "游戏模式",
            ["MineRewind_Details_Seed"] = "种子",
            ["MineRewind_Details_WorldDays"] = "世界天数",
            ["MineRewind_Details_TotalTime"] = "世界总时间",
            ["MineRewind_Details_LastPlayed"] = "最近游玩",
            ["MineRewind_Details_PlayerData"] = "玩家数据",
            ["MineRewind_Details_Yes"] = "是",
            ["MineRewind_Details_No"] = "否",
            ["MineRewind_Details_Format"] = "存档格式",
            ["MineRewind_Details_Format_New"] = "Minecraft 26.1+",
            ["MineRewind_Details_Format_Legacy"] = "旧版 (< 26.1)",
        };

        private static string Localize(string key)
        {
            var cultureName = ResolveLanguageName();
            var preferred = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ZhCnTexts : EnUsTexts;

            if (preferred.TryGetValue(key, out var value))
                return value;

            if (EnUsTexts.TryGetValue(key, out var fallback))
                return fallback;

            return key;
        }

        private static string ResolveLanguageName()
        {
            var overrideLanguage = ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrWhiteSpace(overrideLanguage))
            {
                return overrideLanguage.Trim().Replace('_', '-');
            }

            return CultureInfo.CurrentUICulture.Name;
        }

        private static string LocalizeFormat(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Localize(key), args);
        }
    }
}
