using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Globalization;
using System.Text;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        internal const string SelectedRegionsScopeId = "MineRewind.SelectedRegions";
        internal const int MaxRegionAreaBytes = 32 * 1024;
        internal const int MaxRegionAreaLines = 128;
        internal const int MaxRegionsPerDimension = 4096;
        internal const double MaxBlockCoordinate = 30_000_000d;

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
            "serverconfig"
        };

        public IReadOnlyList<PluginBackupScopeDefinition> GetBackupScopeDefinitions(
            BackupConfig config,
            IReadOnlyDictionary<string, string> settingsValues)
        {
            if (config == null || !CanHandleConfigType(config.ConfigType))
            {
                return Array.Empty<PluginBackupScopeDefinition>();
            }

            return
            [
                new PluginBackupScopeDefinition
                {
                    Id = SelectedRegionsScopeId,
                    DisplayName = Localize("MineRewind_BackupScope_SelectedRegions_Name"),
                    Description = Localize("MineRewind_BackupScope_SelectedRegions_Desc"),
                    Parameters =
                    [
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
                    ]
                }
            ];
        }

        public PluginBackupScopeResolution ResolveBackupScope(
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
                return PluginBackupScopeResolution.Invalid(
                    "invalid_scope_context",
                    Localize("MineRewind_BackupScope_Error_Context"));
            }

            string? worldPath = MinecraftWorldPathResolver.TryResolveWorldPath(folder.Path);
            if (worldPath == null)
            {
                string leafName = Path.GetFileName(
                    (folder.Path ?? string.Empty)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.Equals(leafName, "mods", StringComparison.OrdinalIgnoreCase))
                {
                    return PluginBackupScopeResolution.NotApplicable();
                }

                return PluginBackupScopeResolution.Invalid(
                    "minecraft_world_not_found",
                    LocalizeFormat(
                        "MineRewind_BackupScope_Error_WorldNotFound",
                        folder.Path ?? string.Empty));
            }

            if (!TryBuildRegionBackupWhitelist(
                    folder.Path,
                    worldPath,
                    scope.Parameters,
                    out var rules,
                    out string errorCode,
                    out string errorMessage))
            {
                return PluginBackupScopeResolution.Invalid(errorCode, errorMessage);
            }

            return PluginBackupScopeResolution.Applied(
                new PluginBackupFilterContribution
                {
                    UseWhitelistMode = true,
                    BackupWhitelist = rules
                },
                PluginBackupRuleMergeMode.Replace);
        }

        internal static bool TryBuildRegionBackupWhitelist(
            string sourceRoot,
            string worldPath,
            IReadOnlyDictionary<string, string> parameters,
            out IReadOnlyList<string> rules,
            out string errorCode,
            out string errorMessage)
        {
            rules = Array.Empty<string>();
            errorCode = string.Empty;
            errorMessage = string.Empty;

            if (!TryNormalizeContainedPath(sourceRoot, worldPath, out string normalizedSource, out string normalizedWorld))
            {
                return Fail(
                    "world_outside_source",
                    Localize("MineRewind_BackupScope_Error_WorldOutsideSource"),
                    out errorCode,
                    out errorMessage);
            }

            if (!TryGetSelectedDimensions(parameters, out var dimensions, out errorCode, out errorMessage))
            {
                return false;
            }

            if (!TryParseRegionCoordinates(parameters, out var regionCoordinates, out errorCode, out errorMessage))
            {
                return false;
            }

            if (!TryResolveDimensionRoots(
                    normalizedSource,
                    normalizedWorld,
                    dimensions,
                    out var dimensionRoots,
                    out errorCode,
                    out errorMessage))
            {
                return false;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string essential in RegionBackupEssentialRules)
            {
                AddSourceRelativeRule(result, normalizedSource, normalizedWorld, essential);
            }

            foreach (MinecraftDimension dimension in dimensions)
            {
                string dimensionRoot = dimensionRoots[dimension].Path;
                AddSourceRelativeRule(result, normalizedSource, dimensionRoot, "data");

                foreach ((int regionX, int regionZ) in regionCoordinates)
                {
                    string regionFileName = $"r.{regionX}.{regionZ}.mca";
                    AddSourceRelativeRule(result, normalizedSource, dimensionRoot, $"region/{regionFileName}");
                    AddSourceRelativeRule(result, normalizedSource, dimensionRoot, $"entities/{regionFileName}");
                    AddSourceRelativeRule(result, normalizedSource, dimensionRoot, $"poi/{regionFileName}");
                }

                AddSourceRelativeRule(result, normalizedSource, dimensionRoot, "region/c.*.*.mcc");
                AddSourceRelativeRule(result, normalizedSource, dimensionRoot, "entities/c.*.*.mcc");
                AddSourceRelativeRule(result, normalizedSource, dimensionRoot, "poi/c.*.*.mcc");
            }

            rules = result.OrderBy(rule => rule, StringComparer.OrdinalIgnoreCase).ToArray();
            return true;
        }

        private static bool TryParseRegionCoordinates(
            IReadOnlyDictionary<string, string> parameters,
            out HashSet<(int X, int Z)> coordinates,
            out string errorCode,
            out string errorMessage)
        {
            coordinates = new HashSet<(int X, int Z)>();
            errorCode = string.Empty;
            errorMessage = string.Empty;

            string areaText = GetParameter(parameters, RegionAreasParameterKey);
            if (Encoding.UTF8.GetByteCount(areaText) > MaxRegionAreaBytes)
            {
                return Fail(
                    "region_input_too_large",
                    LocalizeFormat("MineRewind_BackupScope_Error_InputTooLarge", MaxRegionAreaBytes),
                    out errorCode,
                    out errorMessage);
            }

            var lines = SplitRegionAreaLines(areaText).ToList();
            if (lines.Count == 0)
            {
                return Fail(
                    "region_area_required",
                    Localize("MineRewind_BackupScope_Error_AreaRequired"),
                    out errorCode,
                    out errorMessage);
            }

            if (lines.Count > MaxRegionAreaLines)
            {
                return Fail(
                    "region_too_many_lines",
                    LocalizeFormat("MineRewind_BackupScope_Error_TooManyLines", MaxRegionAreaLines),
                    out errorCode,
                    out errorMessage);
            }

            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (!TryParseRegionAreaLine(line, out double x1, out double z1, out double x2, out double z2))
                {
                    return Fail(
                        "invalid_region_area",
                        LocalizeFormat("MineRewind_BackupScope_Error_InvalidArea", index + 1, line),
                        out errorCode,
                        out errorMessage);
                }

                int minRegionX = Math.Min(BlockToRegion(x1), BlockToRegion(x2));
                int maxRegionX = Math.Max(BlockToRegion(x1), BlockToRegion(x2));
                int minRegionZ = Math.Min(BlockToRegion(z1), BlockToRegion(z2));
                int maxRegionZ = Math.Max(BlockToRegion(z1), BlockToRegion(z2));
                long width = (long)maxRegionX - minRegionX + 1L;
                long height = (long)maxRegionZ - minRegionZ + 1L;
                if (width <= 0 || height <= 0 || width * height > MaxRegionsPerDimension)
                {
                    return Fail(
                        "region_limit_exceeded",
                        LocalizeFormat("MineRewind_BackupScope_Error_RegionLimit", MaxRegionsPerDimension),
                        out errorCode,
                        out errorMessage);
                }

                for (int regionX = minRegionX; regionX <= maxRegionX; regionX++)
                {
                    for (int regionZ = minRegionZ; regionZ <= maxRegionZ; regionZ++)
                    {
                        coordinates.Add((regionX, regionZ));
                        if (coordinates.Count > MaxRegionsPerDimension)
                        {
                            return Fail(
                                "region_limit_exceeded",
                                LocalizeFormat("MineRewind_BackupScope_Error_RegionLimit", MaxRegionsPerDimension),
                                out errorCode,
                                out errorMessage);
                        }
                    }
                }
            }

            return true;
        }

        private static bool TryGetSelectedDimensions(
            IReadOnlyDictionary<string, string> parameters,
            out IReadOnlyList<MinecraftDimension> dimensions,
            out string errorCode,
            out string errorMessage)
        {
            var selected = new List<MinecraftDimension>();
            string dimensionsValue = GetParameter(parameters, DimensionsParameterKey);
            if (!string.IsNullOrWhiteSpace(dimensionsValue))
            {
                string[] tokens = dimensionsValue.Split(
                    [';', ',', '|'],
                    StringSplitOptions.TrimEntries);
                foreach (string token in tokens)
                {
                    MinecraftDimension? parsed = ParseDimension(token);
                    if (!parsed.HasValue)
                    {
                        dimensions = Array.Empty<MinecraftDimension>();
                        return Fail(
                            "invalid_dimension",
                            LocalizeFormat("MineRewind_BackupScope_Error_InvalidDimension", token),
                            out errorCode,
                            out errorMessage);
                    }

                    if (!selected.Contains(parsed.Value))
                    {
                        selected.Add(parsed.Value);
                    }
                }
            }
            else
            {
                if (!TryGetBoolParameter(parameters, DimensionOverworldParameterKey, true, out bool overworld)
                    || !TryGetBoolParameter(parameters, DimensionNetherParameterKey, false, out bool nether)
                    || !TryGetBoolParameter(parameters, DimensionEndParameterKey, false, out bool end))
                {
                    dimensions = Array.Empty<MinecraftDimension>();
                    return Fail(
                        "invalid_dimension_flag",
                        Localize("MineRewind_BackupScope_Error_InvalidDimensionFlag"),
                        out errorCode,
                        out errorMessage);
                }

                if (overworld) selected.Add(MinecraftDimension.Overworld);
                if (nether) selected.Add(MinecraftDimension.Nether);
                if (end) selected.Add(MinecraftDimension.End);
            }

            if (selected.Count == 0)
            {
                dimensions = Array.Empty<MinecraftDimension>();
                return Fail(
                    "dimension_required",
                    Localize("MineRewind_BackupScope_Error_DimensionRequired"),
                    out errorCode,
                    out errorMessage);
            }

            dimensions = selected;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            return true;
        }

        private static bool TryResolveDimensionRoots(
            string sourceRoot,
            string worldPath,
            IReadOnlyList<MinecraftDimension> dimensions,
            out IReadOnlyDictionary<MinecraftDimension, DimensionRoot> roots,
            out string errorCode,
            out string errorMessage)
        {
            var resolved = new Dictionary<MinecraftDimension, DimensionRoot>();
            foreach (MinecraftDimension dimension in dimensions)
            {
                var candidates = GetDimensionCandidates(worldPath, dimension)
                    .Where(candidate => Directory.Exists(candidate.Path))
                    .GroupBy(candidate => NormalizeFullPath(candidate.Path), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                bool outsideSourceExists = candidates.Any(candidate =>
                    !BackupStoragePathService.IsPathInsideRoot(candidate.Path, sourceRoot));
                candidates = candidates
                    .Where(candidate => BackupStoragePathService.IsPathInsideRoot(candidate.Path, sourceRoot))
                    .ToList();

                if (candidates.Count == 0)
                {
                    roots = new Dictionary<MinecraftDimension, DimensionRoot>();
                    string code = outsideSourceExists
                        ? "dimension_outside_source"
                        : "dimension_missing";
                    string message = outsideSourceExists
                        ? LocalizeFormat("MineRewind_BackupScope_Error_DimensionOutsideSource", GetDimensionName(dimension))
                        : LocalizeFormat("MineRewind_BackupScope_Error_DimensionMissing", GetDimensionName(dimension));
                    return Fail(code, message, out errorCode, out errorMessage);
                }

                if (candidates.Count > 1)
                {
                    roots = new Dictionary<MinecraftDimension, DimensionRoot>();
                    return Fail(
                        "dimension_layout_ambiguous",
                        LocalizeFormat("MineRewind_BackupScope_Error_DimensionAmbiguous", GetDimensionName(dimension)),
                        out errorCode,
                        out errorMessage);
                }

                resolved[dimension] = candidates[0];
            }

            if (resolved.Values.Select(root => root.Family).Distinct().Count() > 1)
            {
                roots = new Dictionary<MinecraftDimension, DimensionRoot>();
                return Fail(
                    "dimension_layout_mixed",
                    Localize("MineRewind_BackupScope_Error_DimensionMixed"),
                    out errorCode,
                    out errorMessage);
            }

            roots = resolved;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            return true;
        }

        private static IEnumerable<DimensionRoot> GetDimensionCandidates(
            string worldPath,
            MinecraftDimension dimension)
        {
            string post26Path = Path.Combine(
                worldPath,
                "dimensions",
                "minecraft",
                dimension switch
                {
                    MinecraftDimension.Overworld => "overworld",
                    MinecraftDimension.Nether => "the_nether",
                    _ => "the_end"
                });

            if (Directory.Exists(post26Path))
            {
                yield return new DimensionRoot(post26Path, DimensionLayoutFamily.Post26);
            }

            if (dimension == MinecraftDimension.Overworld)
            {
                bool hasLegacyData = Directory.Exists(Path.Combine(worldPath, "region"))
                    || Directory.Exists(Path.Combine(worldPath, "entities"))
                    || Directory.Exists(Path.Combine(worldPath, "poi"));
                if (hasLegacyData || !Directory.Exists(post26Path))
                {
                    yield return new DimensionRoot(worldPath, DimensionLayoutFamily.Legacy);
                }

                yield break;
            }

            string legacyChildName = dimension == MinecraftDimension.Nether ? "DIM-1" : "DIM1";
            string vanillaPath = Path.Combine(worldPath, legacyChildName);
            if (Directory.Exists(vanillaPath))
            {
                yield return new DimensionRoot(vanillaPath, DimensionLayoutFamily.Legacy);
            }

            string? parent = Path.GetDirectoryName(
                worldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string worldName = Path.GetFileName(
                worldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(worldName))
            {
                string suffix = dimension == MinecraftDimension.Nether ? "_nether" : "_the_end";
                string paperPath = Path.Combine(parent, worldName + suffix, legacyChildName);
                if (Directory.Exists(paperPath))
                {
                    yield return new DimensionRoot(paperPath, DimensionLayoutFamily.Legacy);
                }
            }
        }

        private static bool TryNormalizeContainedPath(
            string sourceRoot,
            string worldPath,
            out string normalizedSource,
            out string normalizedWorld)
        {
            normalizedSource = string.Empty;
            normalizedWorld = string.Empty;
            try
            {
                normalizedSource = NormalizeFullPath(sourceRoot);
                normalizedWorld = NormalizeFullPath(worldPath);
                return Directory.Exists(normalizedSource)
                    && Directory.Exists(normalizedWorld)
                    && BackupStoragePathService.IsPathInsideRoot(normalizedWorld, normalizedSource);
            }
            catch
            {
                return false;
            }
        }

        private static void AddSourceRelativeRule(
            HashSet<string> rules,
            string sourceRoot,
            string ruleRoot,
            string relativePath)
        {
            string combined = Path.Combine(
                ruleRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            string relative = Path.GetRelativePath(sourceRoot, combined);
            if (Path.IsPathRooted(relative)
                || string.Equals(relative, "..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated region-backup rule escaped the configured source root.");
            }

            rules.Add(relative
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/'));
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
                string trimmed = line.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed)
                    && !trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    yield return trimmed;
                }
            }
        }

        private static bool TryParseRegionAreaLine(
            string line,
            out double x1,
            out double z1,
            out double x2,
            out double z2)
        {
            x1 = z1 = x2 = z2 = 0;
            string[] parts = line.Split(
                ',',
                StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                return false;
            }

            return TryParseCoordinate(parts[0], out x1)
                && TryParseCoordinate(parts[1], out z1)
                && TryParseCoordinate(parts[2], out x2)
                && TryParseCoordinate(parts[3], out z2);
        }

        private static bool TryParseCoordinate(string value, out double coordinate)
        {
            return double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out coordinate)
                && double.IsFinite(coordinate)
                && coordinate >= -MaxBlockCoordinate
                && coordinate <= MaxBlockCoordinate;
        }

        private static int BlockToRegion(double blockCoordinate)
            => (int)Math.Floor(blockCoordinate / 512d);

        private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
            => parameters != null && parameters.TryGetValue(key, out string? value)
                ? value ?? string.Empty
                : string.Empty;

        private static bool TryGetBoolParameter(
            IReadOnlyDictionary<string, string> parameters,
            string key,
            bool defaultValue,
            out bool result)
        {
            string value = GetParameter(parameters, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                result = defaultValue;
                return true;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "true":
                    result = true;
                    return true;
                case "false":
                    result = false;
                    return true;
                default:
                    result = false;
                    return false;
            }
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

        private static string GetDimensionName(MinecraftDimension dimension)
            => Localize(dimension switch
            {
                MinecraftDimension.Overworld => "MineRewind_BackupScope_DimensionOverworld_Name",
                MinecraftDimension.Nether => "MineRewind_BackupScope_DimensionNether_Name",
                _ => "MineRewind_BackupScope_DimensionEnd_Name"
            });

        private static string NormalizeFullPath(string path)
            => Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool Fail(
            string code,
            string message,
            out string errorCode,
            out string errorMessage)
        {
            errorCode = code;
            errorMessage = message;
            return false;
        }

        internal enum MinecraftDimension
        {
            Overworld,
            Nether,
            End
        }

        private enum DimensionLayoutFamily
        {
            Legacy,
            Post26
        }

        private sealed record DimensionRoot(string Path, DimensionLayoutFamily Family);
    }
}
