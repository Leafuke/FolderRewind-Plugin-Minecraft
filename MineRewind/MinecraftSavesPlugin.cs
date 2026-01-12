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
using System.Globalization;

namespace MineRewind
{
    /// <summary>
    /// Minecraft 存档增强插件
    /// 功能：
    /// 1. 热备份 - 使用 xcopy 创建快照避免文件占用
    /// 2. 批量扫描 - 自动发现 .minecraft 目录下的存档
    /// 3. 配置类型 - 定义 "Minecraft Saves" 类型
    /// </summary>
    public class MinecraftSavesPlugin : IFolderRewindPlugin
    {
        private const string ConfigTypeName = "Minecraft Saves";
        private const string HotBackupSettingKey = "EnableHotBackup";
        private const string SnapshotPathSettingKey = "SnapshotPath";
        private const string CleanupSnapshotSettingKey = "CleanupSnapshot";
        private const string SnapshotDelaySettingKey = "SnapshotDelayMs";

        private bool _enableHotBackup = true;
        private string _snapshotPath = string.Empty;
        private bool _cleanupSnapshot = true;
        private int _snapshotDelayMs = 500;

        // 临时快照路径映射: 原始路径 -> 快照路径
        private readonly Dictionary<string, string> _activeSnapshots = new();

        private static bool IsZh()
        {
            try
            {
                var name = CultureInfo.CurrentUICulture?.Name;
                if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                
            }

            // 兜底 --- Host 侧通常会设置 PrimaryLanguageOverride，但插件不强依赖 WinRT API
            return false;
        }

        private static string T(string zh, string en) => IsZh() ? zh : en;

        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.folderrewind.minerewind",
            Name = T("MineRewind", "MineRewind"),
            Version = "1.0.0",
            Author = "Leafuke",
            Description = T(
                "Minecraft存档备份增强插件：支持热备份、批量扫描.minecraft目录、自动发现存档",
                "Enhanced Minecraft saves backup: hot snapshot backup, batch discovery under .minecraft"),
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
                    DisplayName = T("启用热备份", "Enable hot backup"),
                    Description = T(
                        "备份前使用 xcopy 创建存档快照，避免游戏运行时文件被占用导致备份失败",
                        "Create a snapshot via xcopy before backup to avoid file locks while the game is running"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = SnapshotPathSettingKey,
                    DisplayName = T("快照存储路径", "Snapshot storage path"),
                    Description = T(
                        "热备份快照的临时存储路径，留空则使用系统临时目录",
                        "Temporary snapshot path. Leave empty to use system temp folder"),
                    Type = PluginSettingType.Path,
                    DefaultValue = "",
                    IsRequired = false
                },
                new()
                {
                    Key = CleanupSnapshotSettingKey,
                    DisplayName = T("备份后清理快照", "Clean up snapshot after backup"),
                    Description = T(
                        "备份完成后自动删除临时快照目录",
                        "Automatically delete the temporary snapshot folder after backup"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = SnapshotDelaySettingKey,
                    DisplayName = T("快照延迟(毫秒)", "Snapshot delay (ms)"),
                    Description = T(
                        "创建快照后等待的时间，确保文件系统操作完成",
                        "Wait time after creating snapshot to ensure file system operations complete"),
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
                Message = IsZh()
                    ? $"已创建 {configs.Count} 个 Minecraft 存档配置"
                    : $"Created {configs.Count} Minecraft saves configs"
            };
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
                            T(
                                $"[MineRewind] 检测到 level.dat 被占用，已启用热备份快照：{folder.DisplayName}",
                                $"[MineRewind] Detected level.dat is locked; hot-backup snapshot enabled: {folder.DisplayName}"),
                            "MineRewind");
                    }
                    else
                    {
                        LogService.LogInfo(
                            T(
                                $"[MineRewind] 已创建热备份快照：{folder.DisplayName}",
                                $"[MineRewind] Hot-backup snapshot created: {folder.DisplayName}"),
                            "MineRewind");
                    }

                    return snapshotPath;
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    T(
                        $"[MineRewind] 创建快照失败：{ex.Message}",
                        $"[MineRewind] Failed to create snapshot: {ex.Message}"),
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
                            T(
                                $"[MineRewind] 已清理热备份快照：{folder.DisplayName}",
                                $"[MineRewind] Hot-backup snapshot cleaned up: {folder.DisplayName}"),
                            "MineRewind");
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(
                        T(
                            $"[MineRewind] 清理快照失败：{ex.Message}",
                            $"[MineRewind] Failed to cleanup snapshot: {ex.Message}"),
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
                        LogService.LogWarning(
                            T(
                                $"[MineRewind] 清理旧快照失败：{ex.Message}",
                                $"[MineRewind] Failed to cleanup old snapshot: {ex.Message}"),
                            "MineRewind");
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
                        T(
                            "[MineRewind] 无法启动 xcopy 进程",
                            "[MineRewind] Unable to start xcopy process"),
                        "MineRewind");
                    return null;
                }

                // 等待完成（最多 60 秒）
                var completed = process.WaitForExit(60000);

                if (!completed)
                {
                    try { process.Kill(); } catch { }
                    LogService.LogError(
                        T(
                            "[MineRewind] xcopy 超时",
                            "[MineRewind] xcopy timed out"),
                        "MineRewind");
                    return null;
                }

                var stdOut = process.StandardOutput.ReadToEnd();
                var stdErr = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    LogService.LogWarning(
                        T(
                            $"[MineRewind] xcopy 标准错误输出：{stdErr}",
                            $"[MineRewind] xcopy stderr: {stdErr}"),
                        "MineRewind");
                }

                // 等待文件系统操作完成
                if (_snapshotDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(_snapshotDelayMs);
                }

                // 验证快照是否创建成功
                if (Directory.Exists(snapshotDir) && File.Exists(Path.Combine(snapshotDir, "level.dat")))
                {
                    return snapshotDir;
                }

                LogService.LogWarning(
                    T(
                        "[MineRewind] 快照创建后验证失败",
                        "[MineRewind] Snapshot verification failed after creation"),
                    "MineRewind");
                return null;
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    T(
                        $"[MineRewind] CreateSnapshot 异常：{ex.Message}",
                        $"[MineRewind] CreateSnapshot exception: {ex.Message}"),
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
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return false;
            }
            catch (IOException)
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
