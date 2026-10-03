using System.Globalization;
using System.Text;

namespace MineRewind;

/// <summary>
/// Minecraft 区域备份规则生成器。Host 只持有最终白名单，世界布局、维度和坐标语义全部由 MineRewind 解释。
/// </summary>
internal static class MinecraftRegionBackupScope
{
    internal const int MaxAreaBytes = 32 * 1024;
    internal const int MaxAreaLines = 128;
    internal const int MaxRegionsPerDimension = 4096;
    internal const double MaxBlockCoordinate = 30_000_000d;

    private static readonly string[] EssentialRules =
    [
        "level.dat", "level.dat_old", "icon.png", "resources.zip", "resourcepacks",
        "advancements", "data", "datapacks", "generated", "playerdata", "players",
        "stats", "serverconfig"
    ];

    internal static bool TryBuild(
        string sourceRoot,
        string worldPath,
        IReadOnlyDictionary<string, string> parameters,
        out IReadOnlyList<string> rules,
        out string errorCode)
    {
        rules = Array.Empty<string>();
        errorCode = string.Empty;
        if (!TryNormalizeContainedPath(sourceRoot, worldPath, out var source, out var world))
            return Fail("world_outside_source", out errorCode);
        if (!TryGetDimensions(parameters, out var dimensions, out errorCode)) return false;
        if (!TryGetRegionCoordinates(parameters, out var coordinates, out errorCode)) return false;
        if (!TryResolveDimensionRoots(source, world, dimensions, out var roots, out errorCode)) return false;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var essential in EssentialRules) AddRule(result, source, world, essential);
        foreach (var dimension in dimensions)
        {
            var dimensionRoot = roots[dimension].Path;
            AddRule(result, source, dimensionRoot, "data");
            foreach (var (regionX, regionZ) in coordinates)
            {
                var fileName = $"r.{regionX}.{regionZ}.mca";
                AddRule(result, source, dimensionRoot, $"region/{fileName}");
                AddRule(result, source, dimensionRoot, $"entities/{fileName}");
                AddRule(result, source, dimensionRoot, $"poi/{fileName}");
            }

            // 外置超大区块属于选中 region 的逻辑数据，但文件名不携带 region 坐标，只能纳入对应维度的全部 .mcc。
            AddRule(result, source, dimensionRoot, "region/c.*.*.mcc");
            AddRule(result, source, dimensionRoot, "entities/c.*.*.mcc");
            AddRule(result, source, dimensionRoot, "poi/c.*.*.mcc");
        }

        rules = result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    private static bool TryGetRegionCoordinates(
        IReadOnlyDictionary<string, string> parameters,
        out HashSet<(int X, int Z)> coordinates,
        out string errorCode)
    {
        var areas = Get(parameters, "areas");
        if (areas.Length > 0)
            return TryParseAreas(areas, out coordinates, out errorCode);

        // 兼容 M3R 垂直切片和早期 v3 配置保存的 region 坐标，不把兼容字段重新暴露为 Host 身份。
        var regions = Get(parameters, "regions");
        if (string.IsNullOrWhiteSpace(regions)) regions = Get(parameters, "selectedRegions");
        coordinates = [];
        foreach (var token in regions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var values = token.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
                || Math.Abs((long)x) > ToRegion(MaxBlockCoordinate)
                || Math.Abs((long)z) > ToRegion(MaxBlockCoordinate))
                return Fail("invalid_region_coordinate", out errorCode);
            coordinates.Add((x, z));
            if (coordinates.Count > MaxRegionsPerDimension)
                return Fail("region_limit_exceeded", out errorCode);
        }

        return coordinates.Count > 0
            ? Succeed(out errorCode)
            : Fail("region_area_required", out errorCode);
    }

    private static bool TryParseAreas(
        string text,
        out HashSet<(int X, int Z)> coordinates,
        out string errorCode)
    {
        coordinates = [];
        if (Encoding.UTF8.GetByteCount(text) > MaxAreaBytes)
            return Fail("region_input_too_large", out errorCode);
        var lines = SplitLines(text).ToArray();
        if (lines.Length == 0) return Fail("region_area_required", out errorCode);
        if (lines.Length > MaxAreaLines) return Fail("region_too_many_lines", out errorCode);

        foreach (var line in lines)
        {
            var values = line.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 4
                || !TryCoordinate(values[0], out var x1)
                || !TryCoordinate(values[1], out var z1)
                || !TryCoordinate(values[2], out var x2)
                || !TryCoordinate(values[3], out var z2))
                return Fail("invalid_region_area", out errorCode);

            var minX = Math.Min(ToRegion(x1), ToRegion(x2));
            var maxX = Math.Max(ToRegion(x1), ToRegion(x2));
            var minZ = Math.Min(ToRegion(z1), ToRegion(z2));
            var maxZ = Math.Max(ToRegion(z1), ToRegion(z2));
            var count = ((long)maxX - minX + 1) * ((long)maxZ - minZ + 1);
            if (count <= 0 || count > MaxRegionsPerDimension)
                return Fail("region_limit_exceeded", out errorCode);
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                coordinates.Add((x, z));
                if (coordinates.Count > MaxRegionsPerDimension)
                    return Fail("region_limit_exceeded", out errorCode);
            }
        }

        return Succeed(out errorCode);
    }

    private static bool TryGetDimensions(
        IReadOnlyDictionary<string, string> parameters,
        out IReadOnlyList<MinecraftDimension> dimensions,
        out string errorCode)
    {
        var selected = new List<MinecraftDimension>();
        var compact = Get(parameters, "dimensions");
        if (!string.IsNullOrWhiteSpace(compact))
        {
            foreach (var token in compact.Split([';', ',', '|'], StringSplitOptions.TrimEntries))
            {
                if (!TryParseDimension(token, out var dimension))
                {
                    dimensions = [];
                    return Fail("invalid_dimension", out errorCode);
                }
                if (!selected.Contains(dimension)) selected.Add(dimension);
            }
        }
        else
        {
            if (!TryBoolean(parameters, "dimension.overworld", true, out var overworld)
                || !TryBoolean(parameters, "dimension.nether", false, out var nether)
                || !TryBoolean(parameters, "dimension.end", false, out var end))
            {
                dimensions = [];
                return Fail("invalid_dimension_flag", out errorCode);
            }
            if (overworld) selected.Add(MinecraftDimension.Overworld);
            if (nether) selected.Add(MinecraftDimension.Nether);
            if (end) selected.Add(MinecraftDimension.End);
        }

        dimensions = selected;
        return selected.Count > 0
            ? Succeed(out errorCode)
            : Fail("dimension_required", out errorCode);
    }

    private static bool TryResolveDimensionRoots(
        string source,
        string world,
        IReadOnlyList<MinecraftDimension> dimensions,
        out IReadOnlyDictionary<MinecraftDimension, DimensionRoot> roots,
        out string errorCode)
    {
        var result = new Dictionary<MinecraftDimension, DimensionRoot>();
        foreach (var dimension in dimensions)
        {
            var candidates = GetDimensionCandidates(world, dimension)
                .Where(value => Directory.Exists(value.Path))
                .GroupBy(value => Normalize(value.Path), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var outside = candidates.Any(value => !IsInside(value.Path, source));
            candidates = candidates.Where(value => IsInside(value.Path, source)).ToArray();
            if (candidates.Length == 0)
            {
                roots = new Dictionary<MinecraftDimension, DimensionRoot>();
                return Fail(outside ? "dimension_outside_source" : "dimension_missing", out errorCode);
            }
            if (candidates.Length > 1)
            {
                roots = new Dictionary<MinecraftDimension, DimensionRoot>();
                return Fail("dimension_layout_ambiguous", out errorCode);
            }
            result[dimension] = candidates[0];
        }

        if (result.Values.Select(value => value.Family).Distinct().Count() > 1)
        {
            roots = new Dictionary<MinecraftDimension, DimensionRoot>();
            return Fail("dimension_layout_mixed", out errorCode);
        }
        roots = result;
        return Succeed(out errorCode);
    }

    private static IEnumerable<DimensionRoot> GetDimensionCandidates(string world, MinecraftDimension dimension)
    {
        var post26 = Path.Combine(world, "dimensions", "minecraft", dimension switch
        {
            MinecraftDimension.Overworld => "overworld",
            MinecraftDimension.Nether => "the_nether",
            _ => "the_end"
        });
        if (Directory.Exists(post26)) yield return new(post26, LayoutFamily.Post26);

        if (dimension == MinecraftDimension.Overworld)
        {
            var hasLegacy = Directory.Exists(Path.Combine(world, "region"))
                            || Directory.Exists(Path.Combine(world, "entities"))
                            || Directory.Exists(Path.Combine(world, "poi"));
            if (hasLegacy || !Directory.Exists(post26)) yield return new(world, LayoutFamily.Legacy);
            yield break;
        }

        var child = dimension == MinecraftDimension.Nether ? "DIM-1" : "DIM1";
        var vanilla = Path.Combine(world, child);
        if (Directory.Exists(vanilla)) yield return new(vanilla, LayoutFamily.Legacy);
        var parent = Path.GetDirectoryName(world);
        var name = Path.GetFileName(world);
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) yield break;
        var suffix = dimension == MinecraftDimension.Nether ? "_nether" : "_the_end";
        var paper = Path.Combine(parent, name + suffix, child);
        if (Directory.Exists(paper)) yield return new(paper, LayoutFamily.Legacy);
    }

    private static bool TryNormalizeContainedPath(string source, string world, out string normalizedSource, out string normalizedWorld)
    {
        normalizedSource = normalizedWorld = string.Empty;
        try
        {
            normalizedSource = Normalize(source);
            normalizedWorld = Normalize(world);
            return Directory.Exists(normalizedSource)
                   && Directory.Exists(normalizedWorld)
                   && IsInside(normalizedWorld, normalizedSource);
        }
        catch { return false; }
    }

    private static bool IsInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(Normalize(root), Normalize(candidate));
        return relative == "."
               || (!Path.IsPathRooted(relative)
                   && relative != ".."
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void AddRule(HashSet<string> rules, string source, string root, string relativePath)
    {
        var candidate = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var relative = Path.GetRelativePath(source, candidate);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Generated region backup rule escaped its configured source root.");
        rules.Add(relative.Replace('\\', '/').Trim('/'));
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length > 0 && !value.StartsWith('#')) yield return value;
        }
    }

    private static bool TryCoordinate(string value, out double coordinate)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
           && double.IsFinite(coordinate)
           && coordinate is >= -MaxBlockCoordinate and <= MaxBlockCoordinate;

    private static int ToRegion(double coordinate) => (int)Math.Floor(coordinate / 512d);
    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static bool TryBoolean(IReadOnlyDictionary<string, string> values, string key, bool defaultValue, out bool result)
    {
        var value = Get(values, key);
        if (string.IsNullOrWhiteSpace(value)) { result = defaultValue; return true; }
        return bool.TryParse(value, out result);
    }

    private static bool TryParseDimension(string value, out MinecraftDimension dimension)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "overworld": case "world": case "minecraft:overworld": dimension = MinecraftDimension.Overworld; return true;
            case "nether": case "the_nether": case "minecraft:the_nether": case "dim-1": dimension = MinecraftDimension.Nether; return true;
            case "end": case "the_end": case "minecraft:the_end": case "dim1": dimension = MinecraftDimension.End; return true;
            default: dimension = default; return false;
        }
    }

    private static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    private static bool Fail(string code, out string errorCode) { errorCode = code; return false; }
    private static bool Succeed(out string errorCode) { errorCode = string.Empty; return true; }

    private enum MinecraftDimension { Overworld, Nether, End }
    private enum LayoutFamily { Legacy, Post26 }
    private sealed record DimensionRoot(string Path, LayoutFamily Family);
}
