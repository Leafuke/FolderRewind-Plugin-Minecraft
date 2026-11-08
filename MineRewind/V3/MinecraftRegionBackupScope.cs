using MineRewind.World;

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
        out string errorCode,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        rules = Array.Empty<string>();
        errorCode = string.Empty;
        if (!TryNormalizeContainedPath(sourceRoot, worldPath, out var source, out var world))
            return Fail("world_outside_source", out errorCode);
        if (!MinecraftScopeParameters.TryParse(parameters, out var selection, out errorCode)) return false;
        var dimensions = selection.Dimensions;
        var coordinates = selection.Regions;
        if (!TryResolveDimensionRoots(source, world, dimensions, out var roots, out errorCode, token)) return false;

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var essential in EssentialRules) AddRule(result, source, world, essential);
        foreach (var dimension in dimensions)
        {
            token.ThrowIfCancellationRequested();
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

    private static bool TryResolveDimensionRoots(
        string source,
        string world,
        IReadOnlyList<MinecraftDimension> dimensions,
        out IReadOnlyDictionary<MinecraftDimension, DimensionRoot> roots,
        out string errorCode,
        CancellationToken token)
    {
        var result = new Dictionary<MinecraftDimension, DimensionRoot>();
        DimensionRootsFacts facts;
        string Id(MinecraftDimension dimension) => dimension switch { MinecraftDimension.Overworld => MinecraftPathGrammar.Overworld,
            MinecraftDimension.Nether => MinecraftPathGrammar.Nether, _ => MinecraftPathGrammar.End };
        try { facts = WorldLayoutReader.ResolveDimensionRoots(source, world, dimensions.Select(Id).ToArray(), token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { roots = result; return Fail("dimension_layout_unavailable", out errorCode); }
        foreach (var dimension in dimensions)
        {
            var id = Id(dimension);
            var candidates = facts.Dimensions.Where(d => d.Id == id)
                .Select(d => new DimensionRoot(Path.GetFullPath(Path.Combine(source, d.RelativeRoot)),
                    d.Family == WorldLayoutFamily.Namespaced ? LayoutFamily.Post26 : LayoutFamily.Legacy)).ToArray();
            var outside = facts.Issues.Any(i => i.Code == "dimension_outside_source" && i.DimensionId == id);
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

    private static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    private static bool Fail(string code, out string errorCode) { errorCode = code; return false; }
    private static bool Succeed(out string errorCode) { errorCode = string.Empty; return true; }

    private enum LayoutFamily { Legacy, Post26 }
    private sealed record DimensionRoot(string Path, LayoutFamily Family);
}
