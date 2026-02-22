using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region 插件热键

        public IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions()
        {
            return new List<PluginHotkeyDefinition>
            {
                new()
                {
                    Id = Hotkey_ActiveWorldHotBackup,
                    DisplayName = I18n.GetString("MineRewind_Hotkey_ActiveWorldBackup_Name"),
                    Description = I18n.GetString("MineRewind_Hotkey_ActiveWorldBackup_Desc"),
                    DefaultGesture = "Alt+Ctrl+S",
                    IsGlobalHotkey = true
                },
                new()
                {
                    Id = Hotkey_QuickRestore,
                    DisplayName = "快速还原当前存档",
                    Description = "将当前正在运行的 Minecraft 存档还原到最新备份（需要联动模组支持）",
                    DefaultGesture = "Alt+Ctrl+Z",
                    IsGlobalHotkey = true
                }
            };
        }

        public async Task OnHotkeyInvokedAsync(string hotkeyId, bool isGlobalHotkey, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            if (string.Equals(hotkeyId, Hotkey_ActiveWorldHotBackup, StringComparison.OrdinalIgnoreCase))
            {
                await HandleBackupHotkeyAsync(settingsValues, hostContext);
            }
            else if (string.Equals(hotkeyId, Hotkey_QuickRestore, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRestoreHotkeyAsync(settingsValues, hostContext);
            }
        }

        private async Task HandleBackupHotkeyAsync(IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                Initialize(settingsValues);

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    LogService.LogInfo(I18n.GetString("MineRewind_Hotkey_NoActiveWorld"), "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_backup_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;

                try
                {
                    hostContext?.BroadcastEvent($"event=hotkey_backup_triggered;plugin=minerewind;config={config.Id};world={Uri.EscapeDataString(folder.DisplayName ?? string.Empty)}");
                }
                catch
                {
                }

                await BackupService.BackupFolderAsync(config, folder, "[热键] MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MineRewind_Hotkey_Failed", ex.Message), "MineRewind", ex);
            }
        }

        private async Task HandleRestoreHotkeyAsync(IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                Initialize(settingsValues);

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    LogService.LogInfo("快速还原：未检测到活跃存档", "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_restore_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;
                await TriggerHotRestoreAsync(config, folder);
            }
            catch (Exception ex)
            {
                LogService.LogError($"快速还原热键失败: {ex.Message}", "MineRewind", ex);
            }
        }

        private static (BackupConfig config, ManagedFolder folder)? TryFindOccupiedWorld()
        {
            try
            {
                var configs = ConfigService.CurrentConfig?.BackupConfigs;
                if (configs == null) return null;

                foreach (var cfg in configs)
                {
                    if (cfg == null) continue;
                    if (!string.Equals(cfg.ConfigType, ConfigTypeName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cfg.SourceFolders == null) continue;

                    foreach (var folder in cfg.SourceFolders)
                    {
                        if (folder == null) continue;
                        if (string.IsNullOrWhiteSpace(folder.Path)) continue;
                        if (!Directory.Exists(folder.Path)) continue;

                        if (IsWorldOccupied(folder.Path))
                        {
                            return (cfg, folder);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool IsWorldOccupied(string worldPath)
        {
            try
            {
                var sessionLock = Path.Combine(worldPath, "session.lock");
                if (File.Exists(sessionLock) && IsFileLocked(sessionLock)) return true;

                var dbDir = Path.Combine(worldPath, "db");
                if (Directory.Exists(dbDir))
                {
                    foreach (var entry in Directory.EnumerateFiles(dbDir))
                    {
                        if (IsFileLocked(entry)) return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        #endregion
    }
}
