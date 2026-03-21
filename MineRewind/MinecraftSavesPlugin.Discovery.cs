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
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                results.AddRange(DiscoverFromSavesDirectory(Path.Combine(selectedRootPath, "saves")));
            }
            else if (File.Exists(Path.Combine(selectedRootPath, "level.dat")))
            {
                results.Add(CreateManagedFolder(selectedRootPath));
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
            var coverImage = FindCoverImage(worldPath);

            return new ManagedFolder
            {
                Path = worldPath,
                DisplayName = worldName,
                Description = GetWorldDescription(worldPath),
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

        private IEnumerable<BackupConfig> CreateConfigsFromDotMinecraft(string dotMinecraftPath)
        {
            var configs = new List<BackupConfig>();
            var versionSavesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

            // 收集每个版本目录下的 mods 文件夹路径
            var versionModsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // .minecraft/mods 对应 Default 配置
            var directMods = Path.Combine(dotMinecraftPath, "mods");
            if (Directory.Exists(directMods))
            {
                versionModsMap["Default"] = directMods;
            }

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

                    // 检查版本目录下的 mods 文件夹
                    var versionMods = Path.Combine(versionDir, "mods");
                    if (Directory.Exists(versionMods))
                    {
                        versionModsMap[versionName] = versionMods;
                    }
                }
            }

            foreach (var kvp in versionSavesMap)
            {
                var config = new BackupConfig
                {
                    Name = $"Minecraft - {kvp.Key}",
                    ConfigType = ConfigTypeName,
                    IconGlyph = "\uE7FC",
                };

                foreach (var worldPath in kvp.Value)
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldPath));
                }

                // 添加对应版本的 mods 文件夹
                if (versionModsMap.TryGetValue(kvp.Key, out var modsPath))
                {
                    config.SourceFolders.Add(CreateModsManagedFolder(modsPath));
                }

                config.ExtendedProperties["MinecraftVersion"] = kvp.Key;
                config.ExtendedProperties["Plugin"] = Manifest.Id;
                EnsureRequiredFilters(config);

                configs.Add(config);
            }

            return configs;
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
            config.ExtendedProperties["Plugin"] = Manifest.Id;
            EnsureRequiredFilters(config);

            return config;
        }

        #endregion
    }
}
