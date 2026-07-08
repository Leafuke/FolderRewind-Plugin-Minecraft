using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Globalization;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        private const string SelectedRegionsScopeId = "MineRewind.SelectedRegions";
        private const string RegionAreasParameterKey = "areas";
        private const string DimensionOverworldParameterKey = "dimension.overworld";
        private const string DimensionNetherParameterKey = "dimension.nether";
        private const string DimensionEndParameterKey = "dimension.end";
        private const string DimensionsParameterKey = "dimensions";

        private static readonly string[] RegionBackupEssentialRules =
        {
            "level.dat",
            "level.dat_old",
            "icon.png",
            "resources.zip",
            "resourcepacks",
            "advancements",
            "data",
            "datapacks",
            "generated",
            "playerdata",
            "players",
            "stats",
            "serverconfig",
            "forcedchunks.dat"
        };

        public IReadOnlyList<PluginBackupScopeDefinition> GetBackupScopeDefinitions(
            BackupConfig config,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            if (config == null || !CanHandleConfigType(config.ConfigType))
            {
                return Array.Empty<PluginBackupScopeDefinition>();
            }

            return new[]
            {
                new PluginBackupScopeDefinition
                {
                    Id = SelectedRegionsScopeId,
                    DisplayName = Localize("MineRewind_BackupScope_SelectedRegions_Name"),
                    Description = Localize("MineRewind_BackupScope_SelectedRegions_Desc"),
                    Parameters = new[]
                    {
                        new PluginSettingDefinition
                        {
                            Key = DimensionOverworldParameterKey,
                            DisplayName = Localize("MineRewind_BackupScope_DimensionOverworld_Name"),
                            Description = Localize("MineRewind_BackupScope_DimensionOverworld_Desc"),
                            Type = PluginSettingType.Boolean,
                            DefaultValue = "true"
                        },
                        new PluginSettingDefinition
                        {
                            Key = DimensionNetherParameterKey,
                            DisplayName = Localize("MineRewind_BackupScope_DimensionNether_Name"),
                            Type = PluginSettingType.Boolean,
                            DefaultValue = "false"
                        },
                        new PluginSettingDefinition
                        {
                            Key = DimensionEndParameterKey,
                            DisplayName = Localize("MineRewind_BackupScope_DimensionEnd_Name"),
                            Type = PluginSettingType.Boolean,
                            DefaultValue = "false"
                        },
                        new PluginSettingDefinition
                        {
                            Key = RegionAreasParameterKey,
                            DisplayName = Localize("MineRewind_BackupScope_Areas_Name"),
                            Description = Localize("MineRewind_BackupScope_Areas_Desc"),
                            Type = PluginSettingType.MultilineString,
                            DefaultValue = string.Empty,
                            IsRequired = true
                        }
                    }
                }
            };
        }

        public PluginBackupFilterContribution? GetBackupFilterContribution(
            BackupConfig config,
            ManagedFolder folder,
            PluginBackupScopeContext scope,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            if (config == null
                || folder == null
                || scope == null
                || !CanHandleConfigType(config.ConfigType)
                || !string.Equals(scope.ScopeId, SelectedRegionsScopeId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(folder.Path) || !File.Exists(Path.Combine(folder.Path, "level.dat")))
            {
                return null;
            }

            var rules = BuildRegionBackupWhitelist(folder.Path, scope.Parameters);
            if (rules.Count == 0)
            {
                LogService.LogWarning("[MineRewind] Selected region backup has no valid whitelist rule.", "MineRewind");
                return null;
            }

            return new PluginBackupFilterContribution
            {
                UseWhitelistMode = true,
                BackupWhitelist = rules
            };
        }

        private static IReadOnlyList<string> BuildRegionBackupWhitelist(
            string saveRoot,
            IReadOnlyDictionary<string, string> parameters)
        {
            var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var essential in RegionBackupEssentialRules)
            {
                rules.Add(essential);
            }

            var dimensions = GetSelectedDimensions(parameters);
            var dimensionRoots = ResolveDimensionRoots(saveRoot);
            var areaText = GetParameter(parameters, RegionAreasParameterKey);
            var validAreaCount = 0;

            foreach (var line in SplitRegionAreaLines(areaText))
            {
                if (!TryParseRegionAreaLine(line, out var x1, out var z1, out var x2, out var z2))
                {
                    LogService.LogWarning($"[MineRewind] Invalid selected-region area ignored: {line}", "MineRewind");
                    continue;
                }

                validAreaCount++;
                int minRegionX = Math.Min(BlockToRegion(x1), BlockToRegion(x2));
                int maxRegionX = Math.Max(BlockToRegion(x1), BlockToRegion(x2));
                int minRegionZ = Math.Min(BlockToRegion(z1), BlockToRegion(z2));
                int maxRegionZ = Math.Max(BlockToRegion(z1), BlockToRegion(z2));

                foreach (var dimension in dimensions)
                {
                    if (!dimensionRoots.TryGetValue(dimension, out var root))
                    {
                        continue;
                    }

                    AddDimensionDataRule(rules, root);
                    for (int regionX = minRegionX; regionX <= maxRegionX; regionX++)
                    {
                        for (int regionZ = minRegionZ; regionZ <= maxRegionZ; regionZ++)
                        {
                            string regionFileName = $"r.{regionX}.{regionZ}.mca";
                            // 方块数据、实体数据、兴趣点数据逻辑上是一组，区域备份必须一起纳入。
                            AddDimensionFileRule(rules, root, $"region/{regionFileName}");
                            AddDimensionFileRule(rules, root, $"entities/{regionFileName}");
                            AddDimensionFileRule(rules, root, $"poi/{regionFileName}");
                        }
                    }
                }
            }

            if (validAreaCount == 0)
            {
                LogService.LogWarning("[MineRewind] Selected region backup has no valid area; only essential save files will be included.", "MineRewind");
            }

            return rules.ToList();
        }

        private static IReadOnlyList<MinecraftDimension> GetSelectedDimensions(IReadOnlyDictionary<string, string> parameters)
        {
            var dimensionsValue = GetParameter(parameters, DimensionsParameterKey);
            if (!string.IsNullOrWhiteSpace(dimensionsValue))
            {
                var parsed = dimensionsValue
                    .Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseDimension)
                    .Where(dimension => dimension.HasValue)
                    .Select(dimension => dimension!.Value)
                    .Distinct()
                    .ToList();

                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }

            var selected = new List<MinecraftDimension>();
            if (GetBoolParameter(parameters, DimensionOverworldParameterKey, true))
            {
                selected.Add(MinecraftDimension.Overworld);
            }

            if (GetBoolParameter(parameters, DimensionNetherParameterKey, false))
            {
                selected.Add(MinecraftDimension.Nether);
            }

            if (GetBoolParameter(parameters, DimensionEndParameterKey, false))
            {
                selected.Add(MinecraftDimension.End);
            }

            return selected.Count > 0 ? selected : new[] { MinecraftDimension.Overworld };
        }

        private static Dictionary<MinecraftDimension, string> ResolveDimensionRoots(string saveRoot)
        {
            bool usesNewFormat = NbtHelper.IsPost26_1Format(saveRoot);

            if (usesNewFormat)
            {
                // Minecraft 26.1+ 将原版维度也放入 dimensions/minecraft/*。
                return new Dictionary<MinecraftDimension, string>
                {
                    [MinecraftDimension.Overworld] = "dimensions/minecraft/overworld",
                    [MinecraftDimension.Nether] = "dimensions/minecraft/the_nether",
                    [MinecraftDimension.End] = "dimensions/minecraft/the_end"
                };
            }

            return new Dictionary<MinecraftDimension, string>
            {
                [MinecraftDimension.Overworld] = string.Empty,
                [MinecraftDimension.Nether] = "DIM-1",
                [MinecraftDimension.End] = "DIM1"
            };
        }

        private static void AddDimensionDataRule(HashSet<string> rules, string dimensionRoot)
        {
            AddDimensionFileRule(rules, dimensionRoot, "data");
        }

        private static void AddDimensionFileRule(HashSet<string> rules, string dimensionRoot, string relativePath)
        {
            var normalizedRelative = relativePath.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(dimensionRoot))
            {
                rules.Add(normalizedRelative);
                return;
            }

            rules.Add($"{dimensionRoot.TrimEnd('/', '\\').Replace('\\', '/')}/{normalizedRelative}");
        }

        private static IEnumerable<string> SplitRegionAreaLines(string areaText)
        {
            if (string.IsNullOrWhiteSpace(areaText))
            {
                yield break;
            }

            using var reader = new StringReader(areaText);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return trimmed;
            }
        }

        private static bool TryParseRegionAreaLine(string line, out double x1, out double z1, out double x2, out double z2)
        {
            x1 = z1 = x2 = z2 = 0;

            var parts = line
                .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                return false;
            }

            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x1)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z1)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x2)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z2);
        }

        private static int BlockToRegion(double blockCoordinate)
        {
            // 区域文件覆盖 32x32 区块，即 512x512 方块；Floor 能正确处理 -1 这类负坐标边界。
            return (int)Math.Floor(blockCoordinate / 512d);
        }

        private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        }

        private static bool GetBoolParameter(IReadOnlyDictionary<string, string> parameters, string key, bool defaultValue)
        {
            var value = GetParameter(parameters, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "y" or "on" => true,
                "false" or "0" or "no" or "n" or "off" => false,
                _ => defaultValue
            };
        }

        private static MinecraftDimension? ParseDimension(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "overworld" or "world" or "minecraft:overworld" => MinecraftDimension.Overworld,
                "nether" or "the_nether" or "minecraft:the_nether" or "dim-1" => MinecraftDimension.Nether,
                "end" or "the_end" or "minecraft:the_end" or "dim1" => MinecraftDimension.End,
                _ => null
            };
        }

        private enum MinecraftDimension
        {
            Overworld,
            Nether,
            End
        }
    }
}
