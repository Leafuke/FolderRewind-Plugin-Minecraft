using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region 配置类型与发现

        public IReadOnlyList<string> GetSupportedConfigTypes()
        {
            return new[] { ConfigTypeName };
        }

        public bool CanHandleConfigType(string configType)
        {
            return string.Equals(configType, ConfigTypeName, StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var results = new List<ManagedFolder>();

            if (string.IsNullOrWhiteSpace(selectedRootPath) || !Directory.Exists(selectedRootPath))
                return results;

            var dirName = Path.GetFileName(selectedRootPath);

            if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromDotMinecraft(selectedRootPath));
            }
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromSavesDirectory(selectedRootPath));
            }
            else if (MinecraftWorldPathResolver.TryResolveWorldPath(selectedRootPath) != null)
            {
                // Keep the configured server root as the backup unit. The resolver is
                // only used to recognize roots whose level.dat lives under world/.
                results.Add(CreateManagedFolder(selectedRootPath));
            }
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                results.AddRange(DiscoverFromSavesDirectory(Path.Combine(selectedRootPath, "saves")));
            }

            return results;
        }

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
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Directory.GetParent(selectedRootPath)?.FullName;
                var versionName = parentDir != null ? Path.GetFileName(parentDir) : "Unknown";
                var config = CreateConfigForSavesDir(selectedRootPath, versionName);
                if (config != null)
                    configs.Add(config);
            }
            else if (MinecraftWorldPathResolver.TryResolveWorldPath(selectedRootPath) != null)
            {
                configs.Add(CreateConfigForManagedRoot(selectedRootPath));
            }
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                var config = CreateConfigForVersion(selectedRootPath);
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
                Message = LocalizeFormat("MineRewind_CreateConfigs_Result", configs.Count)
            };
        }

        #endregion

        #region 私有方法 - 目录发现

        private IEnumerable<ManagedFolder> DiscoverFromDotMinecraft(string dotMinecraftPath)
        {
            var results = new List<ManagedFolder>();

            var directSaves = Path.Combine(dotMinecraftPath, "saves");
            if (Directory.Exists(directSaves))
            {
                results.AddRange(DiscoverFromSavesDirectory(directSaves));
            }

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
            var resolvedWorldPath = MinecraftWorldPathResolver.TryResolveWorldPath(worldPath) ?? worldPath;
            var coverImage = FindCoverImage(resolvedWorldPath);

            return new ManagedFolder
            {
                Path = worldPath,
                DisplayName = worldName,
                Description = GetWorldDescription(resolvedWorldPath),
                CoverImagePath = coverImage ?? string.Empty
            };
        }

        private string? FindCoverImage(string worldPath)
        {
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

        /// <summary>
        /// 为 mods 文件夹创建 ManagedFolder
        /// </summary>
        private ManagedFolder CreateModsManagedFolder(string modsPath)
        {
            var parentName = Path.GetFileName(Path.GetDirectoryName(modsPath)) ?? "Unknown";
            return new ManagedFolder
            {
                Path = modsPath,
                DisplayName = $"mods ({parentName})",
                Description = "Minecraft Mods"
            };
        }

        #endregion

        #region 私有方法 - 配置创建

        private BackupConfig CreateConfigForManagedRoot(string selectedRootPath)
        {
            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedRootPath));
            var config = new BackupConfig
            {
                Name = $"Minecraft - {rootName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC"
            };

            config.SourceFolders.Add(CreateManagedFolder(selectedRootPath));
            config.ExtendedProperties["MinecraftVersion"] = "Server";
            config.ExtendedProperties["Plugin"] = Manifest.Id;
            EnsureRequiredFilters(config);
            return config;
        }

        private IEnumerable<BackupConfig> CreateConfigsFromDotMinecraft(string dotMinecraftPath)
        {
            return MinecraftInstanceDiscoveryPlanner
                .DiscoverInstances(dotMinecraftPath, LogDiscoveryWarning)
                .Select(CreateConfigForInstance)
                .ToArray();
        }

        private BackupConfig CreateConfigForInstance(MinecraftInstanceDescriptor instance)
        {
            var config = new BackupConfig
            {
                Name = $"Minecraft - {instance.VersionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC"
            };

            foreach (string worldPath in instance.WorldPaths)
            {
                config.SourceFolders.Add(CreateManagedFolder(worldPath));
            }

            if (!string.IsNullOrWhiteSpace(instance.ModsPath))
            {
                config.SourceFolders.Add(CreateModsManagedFolder(instance.ModsPath));
            }

            config.ExtendedProperties["MinecraftVersion"] = instance.VersionName;
            config.ExtendedProperties["MinecraftInstancePath"] = instance.InstancePath;
            config.ExtendedProperties["Plugin"] = Manifest.Id;
            EnsureRequiredFilters(config);
            return config;
        }

        private BackupConfig? CreateConfigForVersion(string versionDirPath)
        {
            var versionName = Path.GetFileName(versionDirPath);
            var savesPath = Path.Combine(versionDirPath, "saves");

            // 如果没有 saves 也没有 mods，跳过
            var modsPath = Path.Combine(versionDirPath, "mods");
            bool hasSaves = Directory.Exists(savesPath);
            bool hasMods = Directory.Exists(modsPath);

            if (!hasSaves && !hasMods)
                return null;

            var config = new BackupConfig
            {
                Name = $"Minecraft - {versionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC",
            };

            if (hasSaves)
            {
                foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
                {
                    if (File.Exists(Path.Combine(worldDir, "level.dat")))
                    {
                        config.SourceFolders.Add(CreateManagedFolder(worldDir));
                    }
                }
            }

            // 添加 mods 文件夹
            if (hasMods)
            {
                config.SourceFolders.Add(CreateModsManagedFolder(modsPath));
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            config.ExtendedProperties["MinecraftInstancePath"] = MinecraftInstanceDiscoveryPlanner.NormalizePath(versionDirPath);
            config.ExtendedProperties["Plugin"] = Manifest.Id;
            EnsureRequiredFilters(config);

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

            // 检查 saves 的父目录下是否有 mods 文件夹
            var parentDir = Directory.GetParent(savesPath)?.FullName;
            if (parentDir != null)
            {
                var modsPath = Path.Combine(parentDir, "mods");
                if (Directory.Exists(modsPath))
                {
                    config.SourceFolders.Add(CreateModsManagedFolder(modsPath));
                }
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            if (parentDir != null)
            {
                config.ExtendedProperties["MinecraftInstancePath"] = MinecraftInstanceDiscoveryPlanner.NormalizePath(parentDir);
            }
            config.ExtendedProperties["Plugin"] = Manifest.Id;
            EnsureRequiredFilters(config);

            return config;
        }

        #endregion
    }
}
