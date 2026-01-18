using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MineRewind
{
    /// <summary>
    /// Minecraft 存档增强插件
    /// 功能：
    /// 1. 热备份 - 使用 xcopy 创建快照避免文件占用
    /// 2. 批量扫描 - 自动发现 .minecraft 目录下的存档
    /// 3. 配置类型 - 定义 "Minecraft Saves" 类型
    /// </summary>
    public class MinecraftSavesPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider
    {
        private const string ConfigTypeName = "Minecraft Saves";
        private const string HotBackupSettingKey = "EnableHotBackup";
        private const string SnapshotPathSettingKey = "SnapshotPath";
        private const string CleanupSnapshotSettingKey = "CleanupSnapshot";
        private const string SnapshotDelaySettingKey = "SnapshotDelayMs";

        private const string Hotkey_ActiveWorldHotBackup = "hotbackup.active_world";

        private bool _enableHotBackup = true;
        private string _snapshotPath = string.Empty;
        private bool _cleanupSnapshot = true;
        private int _snapshotDelayMs = 500;

        // 临时快照路径映射: 原始路径 -> 快照路径
        private readonly Dictionary<string, string> _activeSnapshots = new();

        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.folderrewind.minerewind",
            Name = "MineRewind",
            Version = "1.1.0",
            Author = "Leafuke",
            Description = "Enhanced Minecraft saves backup: hot snapshot backup, batch discovery under .minecraft",
            LocalizedName = new Dictionary<string, string>
            {
                ["zh-CN"] = "MineRewind",
                ["en-US"] = "MineRewind",
            },
            LocalizedDescription = new Dictionary<string, string>
            {
                ["zh-CN"] = "Minecraft 存档备份增强插件：支持热备份、批量扫描 .minecraft 目录、自动发现存档，以及全局热键触发备份",
                ["en-US"] = "Enhanced Minecraft saves backup: hot snapshot backup, batch discovery under .minecraft, plus a global hotkey trigger",
            },
            EntryAssembly = "MineRewind.dll",
            EntryType = "MineRewind.MinecraftSavesPlugin",
            MinHostVersion = "1.0.0"
        };

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions()
        {
            return new List<PluginSettingDefinition>
            {
                new()
                {
                    Key = HotBackupSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_EnableHotBackup_Name"),
                    Description = I18n.GetString("MineRewind_Setting_EnableHotBackup_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = SnapshotPathSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_SnapshotPath_Name"),
                    Description = I18n.GetString("MineRewind_Setting_SnapshotPath_Desc"),
                    Type = PluginSettingType.Path,
                    DefaultValue = "",
                    IsRequired = false
                },
                new()
                {
                    Key = CleanupSnapshotSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_CleanupSnapshot_Name"),
                    Description = I18n.GetString("MineRewind_Setting_CleanupSnapshot_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = SnapshotDelaySettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_SnapshotDelay_Name"),
                    Description = I18n.GetString("MineRewind_Setting_SnapshotDelay_Desc"),
                    Type = PluginSettingType.Integer,
                    DefaultValue = "500",
                    IsRequired = false
                }
            };
        }

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
        {
            _enableHotBackup = GetBoolSetting(settingsValues, HotBackupSettingKey, true);
            _snapshotPath = GetStringSetting(settingsValues, SnapshotPathSettingKey, string.Empty);
            _cleanupSnapshot = GetBoolSetting(settingsValues, CleanupSnapshotSettingKey, true);
            _snapshotDelayMs = GetIntSetting(settingsValues, SnapshotDelaySettingKey, 500);
        }

        #region 配置类型与发现

        public IReadOnlyList<string> GetSupportedConfigTypes()
        {
            return new[] { ConfigTypeName };
        }

        public bool CanHandleConfigType(string configType)
        {
            return string.Equals(configType, ConfigTypeName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自动发现存档文件夹
        /// </summary>
        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var results = new List<ManagedFolder>();

            if (string.IsNullOrWhiteSpace(selectedRootPath) || !Directory.Exists(selectedRootPath))
                return results;

            var dirName = Path.GetFileName(selectedRootPath);

            // .minecraft
            if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromDotMinecraft(selectedRootPath));
            }
            // saves
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromSavesDirectory(selectedRootPath));
            }
            // versions
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                results.AddRange(DiscoverFromSavesDirectory(Path.Combine(selectedRootPath, "saves")));
            }
            // 存档（包含level.dat）
            else if (File.Exists(Path.Combine(selectedRootPath, "level.dat")))
            {
                results.Add(CreateManagedFolder(selectedRootPath));
            }

            return results;
        }

        /// <summary>
        /// 批量创建配置 - 扫描 .minecraft 目录结构
        /// </summary>
        public PluginCreateConfigResult TryCreateConfigs(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var configs = new List<BackupConfig>();

            if (string.IsNullOrWhiteSpace(selectedRootPath) || !Directory.Exists(selectedRootPath))
            {
                return new PluginCreateConfigResult { Handled = false };
            }

            var dirName = Path.GetFileName(selectedRootPath);

            if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                configs.AddRange(CreateConfigsFromDotMinecraft(selectedRootPath));
            }
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                var config = CreateConfigForVersion(selectedRootPath);
                if (config != null)
                    configs.Add(config);
            }
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Directory.GetParent(selectedRootPath)?.FullName;
                var versionName = parentDir != null ? Path.GetFileName(parentDir) : "Unknown";
                var config = CreateConfigForSavesDir(selectedRootPath, versionName);
                if (config != null)
                    configs.Add(config);
            }

            if (configs.Count == 0)
            {
                return new PluginCreateConfigResult { Handled = false };
            }

            return new PluginCreateConfigResult
            {
                Handled = true,
                CreatedConfigs = configs,
                Message = I18n.Format("MineRewind_CreateConfigs_Result", configs.Count)
            };
        }

        #endregion

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
                }
            };
        }

        public async Task OnHotkeyInvokedAsync(string hotkeyId, bool isGlobalHotkey, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            if (!string.Equals(hotkeyId, Hotkey_ActiveWorldHotBackup, StringComparison.OrdinalIgnoreCase)) return;

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

                // 触发热备份：走 Host 的 BackupService 流程，MineRewind 的 OnBeforeBackupFolder 会自动创建快照
                await BackupService.BackupFolderAsync(config, folder, "[热键] MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MineRewind_Hotkey_Failed", ex.Message), "MineRewind", ex);
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
                // Java 版：session.lock
                var sessionLock = Path.Combine(worldPath, "session.lock");
                if (File.Exists(sessionLock) && IsFileLocked(sessionLock)) return true;

                // 基岩版：可能没有 session.lock，遍历 db 目录下的文件看看有没有被锁定
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

        #region 备份钩子 - 热备份

        /// <summary>
        /// 备份前钩子：创建热备份快照
        /// </summary>
        public string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder, IReadOnlyDictionary<string, string> settingsValues)
        {
            // 重新读取设置
            Initialize(settingsValues);

            // 仅对 Minecraft Saves 类型的配置启用热备份
            if (!CanHandleConfigType(config.ConfigType))
                return null;

            if (!_enableHotBackup)
                return null;

            // 检查是否存在 level.dat (Minecraft 存档的标志文件)
            var levelDatPath = Path.Combine(folder.Path, "level.dat");
            bool isMinecraftSave = File.Exists(levelDatPath);

            if (!isMinecraftSave)
                return null;

            // 检查文件是否被锁定
            bool isLocked = IsFileLocked(levelDatPath);

            // 创建快照
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

        /// <summary>
        /// 备份后钩子：清理快照
        /// </summary>
        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
            // 重新读取设置
            Initialize(settingsValues);

            if (!_cleanupSnapshot)
                return;

            // 清理快照
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

        #region 私有方法 - 目录发现

        private IEnumerable<ManagedFolder> DiscoverFromDotMinecraft(string dotMinecraftPath)
        {
            var results = new List<ManagedFolder>();

            // .minecraft/saves
            var directSaves = Path.Combine(dotMinecraftPath, "saves");
            if (Directory.Exists(directSaves))
            {
                results.AddRange(DiscoverFromSavesDirectory(directSaves));
            }

            // .minecraft/versions/*/saves (多版本隔离模式)
            var versionsDir = Path.Combine(dotMinecraftPath, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                {
                    var versionSaves = Path.Combine(versionDir, "saves");
                    if (Directory.Exists(versionSaves))
                    {
                        results.AddRange(DiscoverFromSavesDirectory(versionSaves));
                    }
                }
            }

            return results;
        }

        private IEnumerable<ManagedFolder> DiscoverFromSavesDirectory(string savesPath)
        {
            var results = new List<ManagedFolder>();

            if (!Directory.Exists(savesPath))
                return results;

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                // 验证是否为有效的 Minecraft 存档 (包含 level.dat)
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    results.Add(CreateManagedFolder(worldDir));
                }
            }

            return results;
        }

        private ManagedFolder CreateManagedFolder(string worldPath)
        {
            var worldName = Path.GetFileName(worldPath);
            var coverImage = FindCoverImage(worldPath);

            return new ManagedFolder
            {
                Path = worldPath,
                DisplayName = worldName,
                Description = GetWorldDescription(worldPath),
                CoverImagePath = coverImage
            };
        }

        private string? FindCoverImage(string worldPath)
        {
            // Minecraft 存档的封面图片通常在 icon.png
            var iconPath = Path.Combine(worldPath, "icon.png");
            if (File.Exists(iconPath))
                return iconPath;

            return null;
        }

        private string GetWorldDescription(string worldPath)
        {
            try
            {
                var levelDat = Path.Combine(worldPath, "level.dat");
                if (File.Exists(levelDat))
                {
                    var fileInfo = new FileInfo(levelDat);
                    var lastModified = fileInfo.LastWriteTime;
                    return $"最后修改: {lastModified:yyyy/MM/dd HH:mm}";
                }
            }
            catch { }

            return "Minecraft 存档";
        }

        #endregion

        #region 私有方法 - 配置创建

        private IEnumerable<BackupConfig> CreateConfigsFromDotMinecraft(string dotMinecraftPath)
        {
            var configs = new List<BackupConfig>();
            var versionSavesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // 收集直接 saves 目录的存档 (归入 "Default" 版本)
            var directSaves = Path.Combine(dotMinecraftPath, "saves");
            if (Directory.Exists(directSaves))
            {
                var worlds = Directory.EnumerateDirectories(directSaves)
                    .Where(d => File.Exists(Path.Combine(d, "level.dat")))
                    .ToList();

                if (worlds.Count > 0)
                {
                    versionSavesMap["Default"] = worlds;
                }
            }

            // versions/*/saves
            var versionsDir = Path.Combine(dotMinecraftPath, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                {
                    var versionName = Path.GetFileName(versionDir);
                    var versionSaves = Path.Combine(versionDir, "saves");

                    if (Directory.Exists(versionSaves))
                    {
                        var worlds = Directory.EnumerateDirectories(versionSaves)
                            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
                            .ToList();

                        if (worlds.Count > 0)
                        {
                            versionSavesMap[versionName] = worlds;
                        }
                    }
                }
            }

            // 为每个版本创建一个配置
            foreach (var kvp in versionSavesMap)
            {
                var config = new BackupConfig
                {
                    Name = $"Minecraft - {kvp.Key}",
                    ConfigType = ConfigTypeName,
                    IconGlyph = "\uE7FC", // 游戏图标
                };

                // 添加存档文件夹
                foreach (var worldPath in kvp.Value)
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldPath));
                }

                config.ExtendedProperties["MinecraftVersion"] = kvp.Key;
                config.ExtendedProperties["Plugin"] = Manifest.Id;

                configs.Add(config);
            }

            return configs;
        }

        private BackupConfig? CreateConfigForVersion(string versionDirPath)
        {
            var versionName = Path.GetFileName(versionDirPath);
            var savesPath = Path.Combine(versionDirPath, "saves");

            if (!Directory.Exists(savesPath))
                return null;

            var config = new BackupConfig
            {
                Name = $"Minecraft - {versionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC",
            };

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldDir));
                }
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            config.ExtendedProperties["Plugin"] = Manifest.Id;

            return config;
        }

        private BackupConfig? CreateConfigForSavesDir(string savesPath, string versionName)
        {
            if (!Directory.Exists(savesPath))
                return null;

            var config = new BackupConfig
            {
                Name = $"Minecraft - {versionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC",
            };

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldDir));
                }
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            config.ExtendedProperties["Plugin"] = Manifest.Id;

            return config;
        }

        #endregion

        #region 私有方法 - 热备份

        /// <summary>
        /// 使用 xcopy 创建存档快照
        /// </summary>
        private string? CreateSnapshot(string sourcePath)
        {
            try
            {
                var worldName = Path.GetFileName(sourcePath);

                // 确定快照目录
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

                // 如果旧快照存在，先清理
                if (Directory.Exists(snapshotDir))
                {
                    try
                    {
                        Directory.Delete(snapshotDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        // 如果无法删除旧快照，使用时间戳创建新目录，避免失败
                        LogService.LogWarning(
                            I18n.Format("MineRewind_Snapshot_CleanupOldFailed", ex.Message),
                            "MineRewind");
                        snapshotDir = Path.Combine(snapshotBaseDir, $"{worldName}_{DateTime.Now:yyyyMMdd_HHmmss}");
                    }
                }

                Directory.CreateDirectory(snapshotDir);

                // 使用 xcopy 复制 (忽略错误继续)
                // /s: 复制子目录
                // /e: 复制空目录
                // /y: 覆盖确认
                // /c: 忽略错误继续
                // /i: 目标是目录
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

                // 先异步读取输出，避免死锁（当缓冲区填满时WaitForExit会永久阻塞）
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                // 等待完成（最多 120 秒）
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

                // 等待文件系统操作完成
                if (_snapshotDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(_snapshotDelayMs);
                }

                // 验证快照是否创建成功
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

        /// <summary>
        /// 检查文件是否被锁定
        /// </summary>
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
                // 文件可能是只读或没有权限
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

                // 支持 "1" / "0" 格式
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
