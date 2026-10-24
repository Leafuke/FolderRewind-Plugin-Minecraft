namespace MineRewind.World;

public static class WorldScanner
{
    public static WorldDescriptor Scan(string sourceRoot, string worldRoot, CancellationToken token = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        worldRoot = WorldPaths.RequireContained(sourceRoot, worldRoot);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string id, string path)
        {
            if (!new[] { "region", "entities", "poi" }.Any(component => Directory.Exists(Path.Combine(path, component)))) return;
            try { path = WorldPaths.RequireContained(sourceRoot, path); }
            catch (IOException) { return; } // Never traverse a sibling outside this source or a linked dimension.
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
            WorldPaths.RequireContained(sourceRoot, dimensionsRoot);
            foreach (var ns in Directory.EnumerateDirectories(dimensionsRoot))
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(ns) & FileAttributes.ReparsePoint) != 0) continue;
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((ns, 0));
                while (pending.TryPop(out var item))
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Depth > 16) throw new IOException("Dimension layout exceeds the depth limit.");
                    if (item.Depth > 0)
                        Add(Path.GetFileName(ns) + ":" + Path.GetRelativePath(ns, item.Path).Replace('\\', '/'), item.Path);
                    foreach (var child in Directory.EnumerateDirectories(item.Path))
                    {
                        if (Path.GetFileName(child) is "region" or "entities" or "poi" or "data") continue;
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push((child, item.Depth + 1));
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
            if (Directory.Exists(regionRoot)) regionRoot = WorldPaths.RequireContained(sourceRoot, regionRoot);
            var regions = new Dictionary<RegionKey, string>();
            foreach (var file in Directory.Exists(regionRoot) ? Directory.EnumerateFiles(regionRoot, "r.*.*.mca") : [])
            {
                token.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                if (WorldPaths.TryRegionName(file, out var key)) regions.Add(key, file);
            }
            result.Add(new(pair.Key, root, regions));
        }
        return new(sourceRoot, worldRoot, result.AsReadOnly());
    }
}
