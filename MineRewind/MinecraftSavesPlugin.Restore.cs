using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
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

            var payload = $"event=handshake;version={FakeVersion};action={action};world={Uri.EscapeDataString(worldName)};min_mod_version={MinModVersion}";

            try
            {
                await KnotLinkService.BroadcastEventAsync(payload).ConfigureAwait(false);
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

            LogService.LogInfo("Mod handshake timed out, no mod detected.", "MineRewind");
            return false;
        }

        #endregion

        #region 热还原主流程

        private async Task TriggerHotRestoreAsync(BackupConfig config, ManagedFolder folder, string? specificBackupFile = null)
        {
            if (Interlocked.CompareExchange(ref _hotRestoreState, RestoreWaitingForMod, RestoreIdle) != RestoreIdle)
            {
                LogService.LogWarning("Hot restore already in progress, ignoring request.", "MineRewind");
                return;
            }

            var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);

            try
            {
                LogService.LogInfo($"Starting hot restore for '{worldName}'...", "MineRewind");

                var handshakeOk = await PerformModHandshakeAsync("restore", worldName);
                if (!handshakeOk || !_modDetected || !_versionCompatible)
                {
                    LogService.LogWarning("Hot restore requires a compatible mod. Aborting.", "MineRewind");
                    KnotLinkService.BroadcastEvent(
                        $"event=restore_cancelled;reason=no_mod;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                await Task.Delay(PostHandshakeDelayMs);

                _worldSaveAndExitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=pre_hot_restore;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                LogService.LogInfo("Waiting for mod to save and exit world...", "MineRewind");
                var exitTask = await Task.WhenAny(_worldSaveAndExitTcs.Task, Task.Delay(WorldExitTimeoutMs));
                if (exitTask != _worldSaveAndExitTcs.Task || !_worldSaveAndExitTcs.Task.Result)
                {
                    LogService.LogWarning("World save and exit timed out, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_cancelled;reason=timeout;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                LogService.LogInfo("Waiting for world files to be released...", "MineRewind");
                if (!await WaitForWorldReleaseAsync(folder.Path, FileReleaseTimeoutMs))
                {
                    LogService.LogWarning("World files still occupied after timeout, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_cancelled;reason=world_occupied;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                var levelDat = Path.Combine(folder.Path, "level.dat");
                if (File.Exists(levelDat))
                {
                    if (!await WaitForFileUnlockedAsync(levelDat, LevelDatReleaseTimeoutMs, LevelDatCheckIntervalMs))
                    {
                        LogService.LogWarning("level.dat still locked, cancelling restore.", "MineRewind");
                        await KnotLinkService.BroadcastEventAsync(
                            $"event=restore_cancelled;reason=world_occupied;world={Uri.EscapeDataString(worldName)}");
                        return;
                    }
                }

                await Task.Delay(500);

                Interlocked.Exchange(ref _hotRestoreState, RestoreRestoring);

                string? latestBackup;
                if (!string.IsNullOrWhiteSpace(specificBackupFile))
                {
                    // RESTORE_CURRENT: 使用指定的备份文件名
                    var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName ?? string.Empty);
                    var fullPath = Path.Combine(backupDir, specificBackupFile);
                    if (!File.Exists(fullPath))
                    {
                        LogService.LogError($"Specified backup file not found: '{specificBackupFile}', aborting restore.", "MineRewind");
                        await KnotLinkService.BroadcastEventAsync(
                            $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                        return;
                    }
                    latestBackup = specificBackupFile;
                }
                else
                {
                    // RESTORE_CURRENT_LATEST: 查找最新备份文件
                    latestBackup = FindLatestBackupFileName(config, folder);
                }
                if (string.IsNullOrEmpty(latestBackup))
                {
                    LogService.LogError($"No backup found for '{worldName}', aborting restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                LogService.LogInfo($"Restoring '{worldName}' from '{latestBackup}'...", "MineRewind");
                try
                {
                    await BackupService.RestoreBackupAsync(config, folder, latestBackup);
                }
                catch (Exception ex)
                {
                    LogService.LogError($"Restore failed: {ex.Message}", "MineRewind", ex);
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                await Task.Delay(100);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=restore_finished;status=success;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                await Task.Delay(PostRestoreStabilizeMs);

                _rejoinTcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=rejoin_world;world={Uri.EscapeDataString(worldName)}");

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

                await KnotLinkService.BroadcastEventAsync(
                    $"event=hot_restore_complete;status={hotRestoreStatus};world={Uri.EscapeDataString(worldName)}");

                LogService.LogInfo($"Hot restore completed: {hotRestoreStatus}", "MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError($"Hot restore error: {ex.Message}", "MineRewind", ex);
                try
                {
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _hotRestoreState, RestoreIdle);
            }
        }

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
                if (!IsFileLocked(filePath))
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
