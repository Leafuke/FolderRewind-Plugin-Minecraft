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
                    DisplayName = Localize("MineRewind_Hotkey_ActiveWorldBackup_Name"),
                    Description = Localize("MineRewind_Hotkey_ActiveWorldBackup_Desc"),
                    DefaultGesture = "Alt+Ctrl+S",
                    IsGlobalHotkey = true
                },
                new()
                {
                    Id = Hotkey_QuickRestore,
                    DisplayName = Localize("MineRewind_Hotkey_QuickRestore_Name"),
                    Description = Localize("MineRewind_Hotkey_QuickRestore_Desc"),
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
                    LogService.LogInfo(Localize("MineRewind_Hotkey_NoActiveWorld"), "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_backup_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;

                // 与 BACKUP_CURRENT 对齐：无论锁检测结果如何，都先强制走一次热备协同流程。
                await RunForcedHotBackupAsync(config, folder, "[热键] MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError(LocalizeFormat("MineRewind_Hotkey_Failed", ex.Message), "MineRewind", ex);
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
                    LogService.LogInfo(Localize("MineRewind_Hotkey_QuickRestore_NoActive"), "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_restore_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;
                await TriggerHotRestoreAsync(config, folder);
            }
            catch (Exception ex)
            {
                LogService.LogError(LocalizeFormat("MineRewind_Hotkey_QuickRestore_Failed", ex.Message), "MineRewind", ex);
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
                if (File.Exists(sessionLock) && FileLockService.IsFileLocked(sessionLock)) return true;

                var dbDir = Path.Combine(worldPath, "db");
                if (Directory.Exists(dbDir))
                {
                    foreach (var entry in Directory.EnumerateFiles(dbDir))
                    {
                        if (FileLockService.IsFileLocked(entry)) return true;
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
