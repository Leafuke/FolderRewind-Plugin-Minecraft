namespace MineRewind.World;

public static class WorldScanner
{
    public static WorldDescriptor Scan(string sourceRoot, string worldRoot, CancellationToken token = default, bool rejectLinks = true)
        => Scan(sourceRoot, worldRoot, token, rejectLinks, new WorldScanBudget());
    internal static WorldDescriptor Scan(string sourceRoot, string worldRoot, CancellationToken token, bool rejectLinks, WorldScanBudget budget)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        worldRoot = WorldPaths.RequireContained(sourceRoot, worldRoot);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string id, string path)
        {
            if (!new[] { "region", "entities", "poi" }.Any(component => Directory.Exists(Path.Combine(path, component)))) return;
            try { path = WorldPaths.RequireContained(sourceRoot, path); }
            catch (IOException)
            {
                var relative = Path.GetRelativePath(sourceRoot, path);
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                { if (rejectLinks) throw; budget.MarkIncomplete(); }
                return; // Never traverse a sibling outside this source.
            }
            if (!candidates.TryGetValue(id, out var paths)) candidates[id] = paths = new(StringComparer.OrdinalIgnoreCase);
            paths.Add(path);
        }
        Add("minecraft:overworld", worldRoot);
        Add("minecraft:the_nether", Path.Combine(worldRoot, "DIM-1"));
        Add("minecraft:the_end", Path.Combine(worldRoot, "DIM1"));
        var parent = Path.GetDirectoryName(worldRoot)!;
        var name = Path.GetFileName(worldRoot);
        Add("minecraft:the_nether", Path.Combine(parent, name + "_nether", "DIM-1"));
        Add("minecraft:the_end", Path.Combine(parent, name + "_the_end", "DIM1"));
        var dimensionsRoot = Path.Combine(worldRoot, "dimensions");
        if (Directory.Exists(dimensionsRoot))
        {
            // Entries enforces containment and applies the strict/display link policy to this directory too.
            foreach (var ns in budget.Entries(sourceRoot, dimensionsRoot, token, rejectLinks))
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(ns)) continue;
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((ns, 0));
                while (pending.TryPop(out var item))
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Depth > 16) throw new IOException("Dimension layout exceeds the depth limit.");
                    if (item.Depth > 0)
                        Add(Path.GetFileName(ns) + ":" + Path.GetRelativePath(ns, item.Path).Replace('\\', '/'), item.Path);
                    foreach (var child in budget.Entries(sourceRoot, item.Path, token, rejectLinks, item.Depth + 1))
                    {
                        if (!Directory.Exists(child)) continue;
                        if (Path.GetFileName(child) is "region" or "entities" or "poi" or "data") continue;
                        if (item.Depth + 1 > 16) throw new WorldScanLimitException("Dimension layout exceeds the depth limit.");
                        pending.Push((child, item.Depth + 1));
                    }
                }
            }
        }
        var result = new List<DimensionDescriptor>();
        foreach (var pair in candidates.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value.Count != 1) throw new IOException("Ambiguous dimension layout: " + pair.Key);
            var root = pair.Value.Single();
            var regionRoot = Path.Combine(root, "region");
            var regions = new Dictionary<RegionKey, string>();
            foreach (var file in Directory.Exists(regionRoot) ? budget.Entries(sourceRoot, regionRoot, token, rejectLinks) : [])
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(file) && WorldPaths.TryRegionName(file, out var key)) regions.Add(key, file);
            }
            result.Add(new(pair.Key, root, regions));
        }
        return new(sourceRoot, worldRoot, result.AsReadOnly()) { IsIncomplete = budget.Incomplete };
    }
}
