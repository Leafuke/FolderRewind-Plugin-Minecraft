using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region 备份钩子 - 热备份

        public string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder, IReadOnlyDictionary<string, string> settingsValues)
        {
            Initialize(settingsValues);

            if (!CanHandleConfigType(config.ConfigType))
                return null;

            if (!_enableHotBackup)
                return null;

            var levelDatPath = Path.Combine(folder.Path, "level.dat");
            bool isMinecraftSave = File.Exists(levelDatPath);

            if (!isMinecraftSave)
                return null;

            bool isLocked = IsFileLocked(levelDatPath);

            if (!isLocked)
                return null;

            if (!KnotLinkService.IsEnabled || !KnotLinkService.IsInitialized)
                return null;

            try
            {
                var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);

                var handshakeOk = PerformModHandshakeSync("backup", worldName);

                if (handshakeOk && _modDetected && _versionCompatible)
                {
                    Thread.Sleep(PostHandshakeDelayMs);

                    KnotLinkService.BroadcastEvent(
                        $"event=pre_hot_backup;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                    _worldSaveTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var saved = _worldSaveTcs.Task.Wait(WorldSaveTimeoutMs);
                    if (saved && _worldSaveTcs.Task.Result)
                    {
                        LogService.LogInfo($"Mod confirmed world save for '{worldName}'", "MineRewind");
                    }
                    else
                    {
                        LogService.LogWarning($"WORLD_SAVED timed out for '{worldName}', proceeding with direct backup", "MineRewind");
                    }
                }
                else
                {
                    LogService.LogInfo("No compatible mod detected, proceeding with direct backup", "MineRewind");
                }
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Mod coordination failed: {ex.Message}, proceeding with direct backup", "MineRewind");
            }

            return null;
        }

        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
        }

        #endregion

        #region 私有方法 - 热备份

        private static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 辅助方法

        private static bool GetBoolSetting(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
        {
            if (settings.TryGetValue(key, out var value))
            {
                if (bool.TryParse(value, out var result))
                    return result;

                if (value == "1") return true;
                if (value == "0") return false;
            }
            return defaultValue;
        }

        private static string GetStringSetting(IReadOnlyDictionary<string, string> settings, string key, string defaultValue)
        {
            if (settings.TryGetValue(key, out var value))
                return value ?? defaultValue;
            return defaultValue;
        }

        private static int GetIntSetting(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
        {
            if (settings.TryGetValue(key, out var value) && int.TryParse(value, out var result))
                return result;
            return defaultValue;
        }

        #endregion
    }
}
