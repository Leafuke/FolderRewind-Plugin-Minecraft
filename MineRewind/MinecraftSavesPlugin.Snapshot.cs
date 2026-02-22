using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
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

            if (isLocked && KnotLinkService.IsEnabled && KnotLinkService.IsInitialized)
            {
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
                            LogService.LogWarning($"WORLD_SAVED timed out for '{worldName}', proceeding with snapshot anyway", "MineRewind");
                        }
                    }
                    else
                    {
                        LogService.LogInfo("No compatible mod detected, creating snapshot directly", "MineRewind");
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning($"Mod coordination failed: {ex.Message}, proceeding with snapshot", "MineRewind");
                }
            }

            try
            {
                var snapshotPath = CreateSnapshot(folder.Path);
                if (!string.IsNullOrWhiteSpace(snapshotPath))
                {
                    _activeSnapshots[folder.Path] = snapshotPath;

                    if (isLocked)
                    {
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Locked", folder.DisplayName),
                            "MineRewind");
                    }
                    else
                    {
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Created", folder.DisplayName),
                            "MineRewind");
                    }

                    return snapshotPath;
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("MineRewind_Snapshot_CreateFailed", ex.Message),
                    "MineRewind",
                    ex);
            }

            return null;
        }

        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
            Initialize(settingsValues);

            if (!_cleanupSnapshot)
                return;

            if (_activeSnapshots.TryGetValue(folder.Path, out var snapshotPath))
            {
                _activeSnapshots.Remove(folder.Path);

                try
                {
                    if (Directory.Exists(snapshotPath))
                    {
                        Directory.Delete(snapshotPath, recursive: true);
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Cleaned", folder.DisplayName),
                            "MineRewind");
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(
                        I18n.Format("MineRewind_Snapshot_CleanupFailed", ex.Message),
                        "MineRewind");
                }
            }
        }

        #endregion

        #region 私有方法 - 热备份

        private string? CreateSnapshot(string sourcePath)
        {
            try
            {
                var worldName = Path.GetFileName(sourcePath);

                string snapshotBaseDir;
                if (!string.IsNullOrWhiteSpace(_snapshotPath) && Directory.Exists(_snapshotPath))
                {
                    snapshotBaseDir = Path.Combine(_snapshotPath, "FolderRewind_Snapshot");
                }
                else
                {
                    snapshotBaseDir = Path.Combine(Path.GetTempPath(), "FolderRewind_Snapshot");
                }

                var snapshotDir = Path.Combine(snapshotBaseDir, worldName);

                if (Directory.Exists(snapshotDir))
                {
                    try
                    {
                        Directory.Delete(snapshotDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogWarning(
                            I18n.Format("MineRewind_Snapshot_CleanupOldFailed", ex.Message),
                            "MineRewind");
                        snapshotDir = Path.Combine(snapshotBaseDir, $"{worldName}_{DateTime.Now:yyyyMMdd_HHmmss}");
                    }
                }

                Directory.CreateDirectory(snapshotDir);

                var xcopyArgs = $"\"{sourcePath}\" \"{snapshotDir}\" /s /e /y /c /i";

                var psi = new ProcessStartInfo
                {
                    FileName = "xcopy",
                    Arguments = xcopyArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    LogService.LogError(
                        I18n.GetString("MineRewind_Xcopy_StartFailed"),
                        "MineRewind");
                    return null;
                }

                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                var completed = process.WaitForExit(120000);

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    LogService.LogError(
                        I18n.GetString("MineRewind_Xcopy_Timeout"),
                        "MineRewind");
                    return null;
                }

                var stdOut = stdOutTask.GetAwaiter().GetResult();
                var stdErr = stdErrTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    LogService.LogWarning(
                        I18n.Format("MineRewind_Xcopy_Stderr", stdErr),
                        "MineRewind");
                }

                if (_snapshotDelayMs > 0)
                {
                    Thread.Sleep(_snapshotDelayMs);
                }

                if (Directory.Exists(snapshotDir))
                {
                    return snapshotDir;
                }

                LogService.LogWarning(
                    I18n.GetString("MineRewind_Snapshot_VerifyFailed"),
                    "MineRewind");
                return null;
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("MineRewind_Snapshot_Exception", ex.Message),
                    "MineRewind",
                    ex);
                return null;
            }
        }

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
