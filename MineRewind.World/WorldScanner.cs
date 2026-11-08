namespace MineRewind.World;

public static class WorldScanner
{
    public static WorldDescriptor Scan(string sourceRoot, string worldRoot, CancellationToken token = default, bool rejectLinks = true)
        => Scan(sourceRoot, worldRoot, token, rejectLinks, new WorldScanBudget());

    internal static WorldDescriptor Scan(string sourceRoot, string worldRoot, CancellationToken token, bool rejectLinks, WorldScanBudget budget)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        worldRoot = WorldPaths.RequireContained(sourceRoot, worldRoot);
        var facts = WorldLayoutReader.Read(sourceRoot, worldRoot, rejectLinks, budget, token);
        // Preview accepts only dimensions containing at least one component directory.
        var candidates = facts.Dimensions.Where(d => d.ComponentDirectories.Count > 0).ToArray();
        var result = new List<DimensionDescriptor>();
        foreach (var group in candidates.GroupBy(d => d.Id, StringComparer.Ordinal))
        {
            if (group.Count() != 1) throw new IOException("Ambiguous dimension layout: " + group.Key);
            var dimension = group.Single();
            var regions = dimension.RegionFiles.Where(f => f.Component == ChunkComponentKind.Terrain)
                .ToDictionary(f => f.Region, f => Path.Combine(sourceRoot, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            result.Add(new(dimension.Id, Path.GetFullPath(Path.Combine(sourceRoot, dimension.RelativeRoot)), regions));
        }
        return new(sourceRoot, worldRoot, result.AsReadOnly()) { IsIncomplete = !facts.IsComplete };
    }
}
