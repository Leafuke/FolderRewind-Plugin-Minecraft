using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region 还原钩子 - 保留玩家数据

        /// <summary>
        /// 还原前钩子：提取当前 level.dat 中的玩家数据（位置、物品栏等）。
        /// </summary>
        public object? OnBeforeRestoreFolder(BackupConfig config, ManagedFolder folder, string archiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
            Initialize(settingsValues);

            if (!CanHandleConfigType(config.ConfigType))
                return null;

            if (!_preservePlayerData && !_forcePreserveNextRestore)
                return null;

            var levelDatPath = Path.Combine(folder.Path, "level.dat");
            if (!File.Exists(levelDatPath))
                return null;

            LogService.LogInfo($"[MineRewind] Extracting player data before restore for '{folder.DisplayName}'...", "MineRewind");
            var snapshot = NbtHelper.ExtractPlayerData(folder.Path);
            return snapshot;
        }

        /// <summary>
        /// 还原后钩子：将之前保存的玩家数据写回 level.dat。
        /// </summary>
        public void OnAfterRestoreFolder(BackupConfig config, ManagedFolder folder, bool success, string archiveFileName, object? state, IReadOnlyDictionary<string, string> settingsValues)
        {
            if (!success || state == null)
                return;

            if (!CanHandleConfigType(config.ConfigType))
                return;

            if (state is not NbtHelper.PlayerDataSnapshot snapshot)
                return;

            LogService.LogInfo($"[MineRewind] Applying preserved player data after restore for '{folder.DisplayName}'...", "MineRewind");
            var applied = NbtHelper.ApplyPlayerData(folder.Path, snapshot);
            if (applied)
            {
                LogService.LogInfo($"[MineRewind] Player data preserved successfully for '{folder.DisplayName}'.", "MineRewind");
            }
            else
            {
                LogService.LogWarning($"[MineRewind] Failed to apply preserved player data for '{folder.DisplayName}'.", "MineRewind");
            }
        }

        #endregion

        #region 模组握手与热还原

        #region 握手流程

        private bool PerformModHandshakeSync(string action, string worldName)
        {
            return PerformModHandshakeAsync(action, worldName, HandshakeTimeoutMs)
                .GetAwaiter().GetResult();
        }

        private async Task<bool> PerformModHandshakeAsync(string action, string worldName, int timeoutMs = HandshakeTimeoutMs)
        {
            _modDetected = false;
            _modVersion = string.Empty;
            _versionCompatible = false;
            var pendingHandshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handshakeTcs = pendingHandshake;

            var modWorldName = FormatModInteropValue(worldName);

            try
            {
                await KnotLinkService.BroadcastEventAsync(null, "handshake", new Dictionary<string, string?>
                {
                    ["version"] = FakeVersion,
                    ["action"] = action,
                    ["world"] = modWorldName,
                    ["min_mod_version"] = MinModVersion
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Handshake broadcast failed: {ex.Message}", "MineRewind");
                return false;
            }

            try
            {
                var delayTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(pendingHandshake.Task, delayTask).ConfigureAwait(false);

                if (completedTask == pendingHandshake.Task)
                {
                    return pendingHandshake.Task.Result;
                }
            }
            catch { }
            finally
            {
                if (ReferenceEquals(_handshakeTcs, pendingHandshake))
                {
                    _handshakeTcs = null;
                }
            }

            LogService.LogWarning(
                $"Mod handshake timed out, no mod detected. action={action}, world={modWorldName}, min_mod_version={MinModVersion}",
                "MineRewind");
            return false;
        }

        #endregion

        #region 热还原主流程

        private async Task TriggerHotRestoreAsync(BackupConfig config, ManagedFolder folder, string? specificBackupFile = null, bool forcePreservePlayerData = false)
        {
            if (Interlocked.CompareExchange(ref _hotRestoreState, RestoreWaitingForMod, RestoreIdle) != RestoreIdle)
            {
                LogService.LogWarning("Hot restore already in progress, ignoring request.", "MineRewind");
                return;
            }

            var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);
            _forcePreserveNextRestore = forcePreservePlayerData;

            try
            {
                LogService.LogInfo($"Starting hot restore for '{worldName}'...", "MineRewind");

                var handshakeOk = await PerformModHandshakeAsync("restore", worldName);
                if (!handshakeOk || !_modDetected || !_versionCompatible)
                {
                    LogService.LogWarning("Hot restore requires a compatible mod. Aborting.", "MineRewind");
                    KnotLinkService.BroadcastEvent(null, "restore_cancelled", new Dictionary<string, string?>
                    {
                        ["reason"] = "no_mod",
                        ["world"] = FormatModInteropValue(worldName)
                    });
                    return;
                }

                await Task.Delay(PostHandshakeDelayMs);

                _worldSaveAndExitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(null, "pre_hot_restore", new Dictionary<string, string?>
                {
                    ["config"] = config.Id,
                    ["world"] = FormatModInteropValue(worldName)
                });

                LogService.LogInfo("Waiting for mod to save and exit world...", "MineRewind");
                var exitTask = await Task.WhenAny(_worldSaveAndExitTcs.Task, Task.Delay(WorldExitTimeoutMs));
                if (exitTask != _worldSaveAndExitTcs.Task || !_worldSaveAndExitTcs.Task.Result)
                {
                    LogService.LogWarning("World save and exit timed out, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(null, "restore_cancelled", new Dictionary<string, string?>
                    {
                        ["reason"] = "timeout",
                        ["world"] = FormatModInteropValue(worldName)
                    });
                    return;
                }

                LogService.LogInfo("Waiting for world files to be released...", "MineRewind");
                if (!await WaitForWorldReleaseAsync(folder.Path, FileReleaseTimeoutMs))
                {
                    LogService.LogWarning("World files still occupied after timeout, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(null, "restore_cancelled", new Dictionary<string, string?>
                    {
                        ["reason"] = "world_occupied",
                        ["world"] = FormatModInteropValue(worldName)
                    });
                    return;
                }

                var levelDat = Path.Combine(folder.Path, "level.dat");
                if (File.Exists(levelDat))
                {
                    if (!await WaitForFileUnlockedAsync(levelDat, LevelDatReleaseTimeoutMs, LevelDatCheckIntervalMs))
                    {
                        LogService.LogWarning("level.dat still locked, cancelling restore.", "MineRewind");
                        await KnotLinkService.BroadcastEventAsync(null, "restore_cancelled", new Dictionary<string, string?>
                        {
                            ["reason"] = "world_occupied",
                            ["world"] = FormatModInteropValue(worldName)
                        });
                        return;
                    }
                }

                await Task.Delay(500);

                Interlocked.Exchange(ref _hotRestoreState, RestoreRestoring);

                string? latestBackup;
                if (!string.IsNullOrWhiteSpace(specificBackupFile))
                {
                    // RESTORE + current_save + file: 使用指定的备份文件名
                    var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName ?? string.Empty);
                    var fullPath = Path.Combine(backupDir, specificBackupFile);
                    if (!File.Exists(fullPath))
                    {
                        LogService.LogError($"Specified backup file not found: '{specificBackupFile}', aborting restore.", "MineRewind");
                        await BroadcastRestoreFinishedAsync("failure", config.Id, worldName);
                        return;
                    }
                    latestBackup = specificBackupFile;
                }
                else
                {
                    // RESTORE + current_save without file: 查找最新备份文件
                    latestBackup = FindLatestBackupFileName(config, folder);
                }
                if (string.IsNullOrEmpty(latestBackup))
                {
                    LogService.LogError($"No backup found for '{worldName}', aborting restore.", "MineRewind");
                    await BroadcastRestoreFinishedAsync("failure", config.Id, worldName);
                    return;
                }

                LogService.LogInfo($"Restoring '{worldName}' from '{latestBackup}'...", "MineRewind");
                try
                {
                    await BackupService.RestoreBackupAsync(config, folder, latestBackup, BackupService.RestoreMode.Clean);
                }
                catch (Exception ex)
                {
                    LogService.LogError($"Restore failed: {ex.Message}", "MineRewind", ex);
                    await BroadcastRestoreFinishedAsync("failure", config.Id, worldName);
                    return;
                }

                await Task.Delay(100);
                await BroadcastRestoreFinishedAsync("success", config.Id, worldName);

                await Task.Delay(PostRestoreStabilizeMs);

                _rejoinTcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(null, "rejoin_world", new Dictionary<string, string?>
                {
                    ["world"] = FormatModInteropValue(worldName)
                });

                LogService.LogInfo("Waiting for mod to rejoin world...", "MineRewind");
                string hotRestoreStatus;
                var rejoinTask = await Task.WhenAny(_rejoinTcs.Task, Task.Delay(RejoinTimeoutMs));

                if (rejoinTask == _rejoinTcs.Task)
                {
                    var (success, reason) = _rejoinTcs.Task.Result;
                    hotRestoreStatus = success ? "full_success" : "restore_ok_rejoin_failed";
                    if (!success)
                    {
                        LogService.LogWarning($"Rejoin failed: {reason}", "MineRewind");
                    }
                }
                else
                {
                    hotRestoreStatus = "restore_ok_rejoin_timeout";
                    LogService.LogWarning("Rejoin world timed out.", "MineRewind");
                }

                await KnotLinkService.BroadcastEventAsync(null, "hot_restore_complete", new Dictionary<string, string?>
                {
                    ["status"] = hotRestoreStatus,
                    ["world"] = FormatModInteropValue(worldName)
                });

                LogService.LogInfo($"Hot restore completed: {hotRestoreStatus}", "MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError($"Hot restore error: {ex.Message}", "MineRewind", ex);
                try
                {
                    await BroadcastRestoreFinishedAsync("failure", config.Id, worldName);
                }
                catch { }
            }
            finally
            {
                _forcePreserveNextRestore = false;
                Interlocked.Exchange(ref _hotRestoreState, RestoreIdle);
            }
        }

        private static Task BroadcastRestoreFinishedAsync(string status, string configId, string worldName) =>
            KnotLinkService.BroadcastEventAsync(null, "restore_finished", new Dictionary<string, string?>
            {
                ["status"] = status,
                ["config"] = configId,
                ["world"] = FormatModInteropValue(worldName)
            });

        #endregion

        #region 热还原辅助工具

        private static async Task<bool> WaitForWorldReleaseAsync(string worldPath, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsWorldOccupied(worldPath))
                    return true;

                await Task.Delay(FileLockCheckIntervalMs);
            }
            return false;
        }

        private static async Task<bool> WaitForFileUnlockedAsync(string filePath, int timeoutMs, int intervalMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!FileLockService.IsFileLocked(filePath))
                    return true;

                await Task.Delay(intervalMs);
            }
            return false;
        }

        private static string? FindLatestBackupFileName(BackupConfig config, ManagedFolder folder)
        {
            try
            {
                var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName ?? string.Empty);
                if (!Directory.Exists(backupDir))
                    return null;

                var extensions = new[] { ".7z", ".zip" };
                var files = Directory.GetFiles(backupDir)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(Path.GetFileName)
                    .FirstOrDefault();

                return files;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Failed to find latest backup: {ex.Message}", "MineRewind");
                return null;
            }
        }

        private static bool IsModVersionCompatible(string currentVer, string requiredVer)
        {
            try
            {
                var current = ParseVersion(currentVer);
                var required = ParseVersion(requiredVer);
                return current.CompareTo(required) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static (int Major, int Minor, int Patch) ParseVersion(string v)
        {
            var parts = v.Split('.');
            int major = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
            int patch = parts.Length > 2 && int.TryParse(parts[2], out var pa) ? pa : 0;
            return (major, minor, patch);
        }

        #endregion

        #endregion
    }
}
