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
            return OnBeforeBackupFolder(config, folder, BackupInvocationOptions.Default, settingsValues);
        }

        public string? OnBeforeBackupFolder(
            BackupConfig config,
            ManagedFolder folder,
            BackupInvocationOptions invocationOptions,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            Initialize(settingsValues);

            if (!CanHandleConfigType(config.ConfigType))
                return null;

            var levelDatPath = Path.Combine(folder.Path, "level.dat");
            var sessionLockPath = Path.Combine(folder.Path, "session.lock");
            bool isMinecraftSave = File.Exists(levelDatPath);

            if (!isMinecraftSave)
                return null;

            bool forceHotBackup = IsForceHotBackupRequested(folder.Path);

            bool isLocked = FileLockService.IsFileLocked(levelDatPath) || FileLockService.IsFileLocked(sessionLockPath);

            bool preferConsistentSnapshot = invocationOptions?.PreferApplicationConsistentSnapshot == true;
            bool shouldCoordinate = forceHotBackup || isLocked || preferConsistentSnapshot;

            if (!shouldCoordinate)
                return null;

            if (!KnotLinkService.IsEnabled || !KnotLinkService.IsInitialized)
            {
                LogHotBackupFallback(
                    folder,
                    invocationOptions,
                    "KnotLink is disabled or not initialized");
                return null;
            }

            try
            {
                var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);

                var handshakeOk = PerformModHandshakeSync("backup", worldName);

                if (handshakeOk && _modDetected && _versionCompatible)
                {
                    if ((forceHotBackup || preferConsistentSnapshot) && !isLocked)
                    {
                        LogService.LogInfo($"Hot-backup coordination requested for '{worldName}' before diff check. source={invocationOptions?.Source}", "MineRewind");
                    }

                    Thread.Sleep(PostHandshakeDelayMs);

                    var pendingWorldSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _worldSaveTcs = pendingWorldSave;

                    KnotLinkService.BroadcastEvent(
                        $"event=pre_hot_backup;config={config.Id};world={FormatModInteropValue(worldName)}");

                    var saved = pendingWorldSave.Task.Wait(WorldSaveTimeoutMs);
                    if (saved && pendingWorldSave.Task.Result)
                    {
                        LogService.LogInfo($"Mod confirmed world save for '{worldName}'", "MineRewind");
                    }
                    else
                    {
                        LogService.LogWarning($"WORLD_SAVED timed out for '{worldName}', proceeding with direct backup", "MineRewind");
                    }

                    if (ReferenceEquals(_worldSaveTcs, pendingWorldSave))
                    {
                        _worldSaveTcs = null;
                    }
                }
                else
                {
                    LogHotBackupFallback(
                        folder,
                        invocationOptions,
                        "No compatible mod detected");
                }
            }
            catch (Exception ex)
            {
                LogHotBackupFallback(
                    folder,
                    invocationOptions,
                    $"Mod coordination failed: {ex.Message}");
            }

            return null;
        }

        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
        }

        #endregion

        #region 私有方法 - 热备份

        private static void LogHotBackupFallback(
            ManagedFolder folder,
            BackupInvocationOptions? invocationOptions,
            string reason)
        {
            var worldName = folder?.DisplayName ?? Path.GetFileName(folder?.Path ?? string.Empty);
            LogService.LogWarning(
                $"Hot-backup coordination unavailable for '{worldName}'. source={invocationOptions?.Source}, reason={reason}. Proceeding with direct backup.",
                "MineRewind");
        }

        private async Task RunForcedHotBackupAsync(
            BackupConfig config,
            ManagedFolder folder,
            string comment,
            bool forceFullBackup = false,
            BackupInvocationOptions? invocationOptions = null)
        {
            MarkForceHotBackup(folder.Path);
            try
            {
                await BackupService.BackupFolderAsync(
                    config,
                    folder,
                    comment,
                    forceFullBackup,
                    invocationOptions ?? BackupInvocationOptions.ForRemote());
            }
            finally
            {
                ClearForceHotBackup(folder.Path);
            }
        }

        private void MarkForceHotBackup(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            _forceHotBackupFolders.AddOrUpdate(folderPath, 1, static (_, current) => current + 1);
        }

        private void ClearForceHotBackup(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            while (true)
            {
                if (!_forceHotBackupFolders.TryGetValue(folderPath, out var current))
                    return;

                if (current <= 1)
                {
                    if (_forceHotBackupFolders.TryRemove(folderPath, out _))
                        return;
                }
                else if (_forceHotBackupFolders.TryUpdate(folderPath, current - 1, current))
                {
                    return;
                }
            }
        }

        private bool IsForceHotBackupRequested(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return false;

            return _forceHotBackupFolders.TryGetValue(folderPath, out var current) && current > 0;
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
