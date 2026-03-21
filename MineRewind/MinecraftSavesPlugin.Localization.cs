using System.Globalization;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        private static readonly IReadOnlyDictionary<string, string> EnUsTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MineRewind_Setting_EnableHotBackup_Name"] = "Enable hot backup",
            ["MineRewind_Setting_EnableHotBackup_Desc"] = "Conmunicate with MineBackup. Ensure perfect performance.",
            ["MineRewind_Setting_PreservePlayerData_Name"] = "Preserve player data on restore",
            ["MineRewind_Setting_PreservePlayerData_Desc"] = "Keep the current player's position, inventory, XP, etc. when restoring a save (single-player only). Usually not needed.",
            ["MineRewind_CreateConfigs_Result"] = "Created {0} Minecraft saves configs",
            ["MineRewind_Hotkey_ActiveWorldBackup_Name"] = "Hot backup the active world",
            ["MineRewind_Hotkey_ActiveWorldBackup_Desc"] = "Detect the running Minecraft world (locked files) and trigger a hot backup.",
            ["MineRewind_Hotkey_QuickRestore_Name"] = "Quick restore current save",
            ["MineRewind_Hotkey_QuickRestore_Desc"] = "Restore the currently running Minecraft save to the latest backup (requires companion mod)",
            ["MineRewind_Hotkey_NoActiveWorld"] = "[MineRewind] No active world detected (no locked save found).",
            ["MineRewind_Hotkey_Failed"] = "[MineRewind] Hotkey-triggered backup failed: {0}",
            ["MineRewind_Hotkey_QuickRestore_NoActive"] = "[MineRewind] Quick restore: no active save detected",
            ["MineRewind_Hotkey_QuickRestore_Failed"] = "[MineRewind] Quick restore hotkey failed: {0}",
            ["MineRewind_KnotLink_BackupCurrent_Failed"] = "[MineRewind] KnotLink BACKUP_CURRENT failed: {0}",
        };

        private static readonly IReadOnlyDictionary<string, string> ZhCnTexts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MineRewind_Setting_EnableHotBackup_Name"] = "启用热备份",
            ["MineRewind_Setting_EnableHotBackup_Desc"] = "与联动模组说悄悄话，确保热备份的完美进行~",
            ["MineRewind_Setting_PreservePlayerData_Name"] = "还原时保留玩家数据",
            ["MineRewind_Setting_PreservePlayerData_Desc"] = "还原存档时**不还原**当前玩家的位置、物品栏、经验等数据（仅单人存档）。一般不需要开启。",
            ["MineRewind_CreateConfigs_Result"] = "已创建 {0} 个 Minecraft 存档配置",
            ["MineRewind_Hotkey_ActiveWorldBackup_Name"] = "热备份正在运行的世界",
            ["MineRewind_Hotkey_ActiveWorldBackup_Desc"] = "检测正在运行（文件被占用）的 Minecraft 存档，并触发热备份。",
            ["MineRewind_Hotkey_QuickRestore_Name"] = "快速还原当前存档",
            ["MineRewind_Hotkey_QuickRestore_Desc"] = "将当前正在运行的 Minecraft 存档还原到最新备份（需要联动模组支持）",
            ["MineRewind_Hotkey_NoActiveWorld"] = "[MineRewind] 未检测到正在运行的世界（未发现被占用的存档）。",
            ["MineRewind_Hotkey_Failed"] = "[MineRewind] 热键触发备份失败：{0}",
            ["MineRewind_Hotkey_QuickRestore_NoActive"] = "[MineRewind] 快速还原：未检测到活跃存档",
            ["MineRewind_Hotkey_QuickRestore_Failed"] = "[MineRewind] 快速还原热键失败：{0}",
            ["MineRewind_KnotLink_BackupCurrent_Failed"] = "[MineRewind] KnotLink BACKUP_CURRENT 失败：{0}",
        };

        private static string Localize(string key)
        {
            var cultureName = CultureInfo.CurrentUICulture.Name;
            var preferred = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? ZhCnTexts : EnUsTexts;

            if (preferred.TryGetValue(key, out var value))
                return value;

            if (EnUsTexts.TryGetValue(key, out var fallback))
                return fallback;

            return key;
        }

        private static string LocalizeFormat(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Localize(key), args);
        }
    }
}