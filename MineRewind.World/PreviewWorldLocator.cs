namespace MineRewind.World;

/// <summary>Preview-only recognition of an incomplete tree. Backup/restore WorldLocator semantics stay unchanged.</summary>
public static class PreviewWorldLocator
{
    public static (string? Path, bool Ambiguous) Resolve(string configuredRoot, CancellationToken token = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        if (!Directory.Exists(root)) return (null, false);
        WorldPaths.RequireContained(root, root);
        if (HasLevel(root)) return (root, false);
        var properties = Path.Combine(root, "server.properties");
        if (File.Exists(properties))
        {
            WorldPaths.RequireContained(root, properties);
            if (new FileInfo(properties).Length > 65536) return (null, false);
            foreach (var line in File.ReadLines(properties))
            {
                token.ThrowIfCancellationRequested();
                var value = line.Trim();
                if (value.StartsWith('#') || value.StartsWith('!')) continue;
                var separator = value.IndexOf('=');
                if (separator < 0 || !value[..separator].Trim().Equals("level-name", StringComparison.OrdinalIgnoreCase)) continue;
                var name = value[(separator + 1)..].Trim();
                if (name.Length == 0) break;
                if (Path.IsPathRooted(name)) return (null, false);
                var candidate = Path.GetFullPath(Path.Combine(root, name));
                var relative = Path.GetRelativePath(root, candidate);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !Directory.Exists(candidate)) return (null, false);
                var child = WorldPaths.RequireContained(root, candidate);
                return (HasLevel(child) || HasStructure(root, child, token) ? child : null, false);
            }
        }
        if (HasStructure(root, root, token)) return (root, false);
        var candidates = new List<string>();
        var visited = 0;
        foreach (var child in Directory.EnumerateDirectories(root))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 4096) return (null, true);
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            if (Path.GetFileName(child) is "DIM-1" or "DIM1" or "dimensions" or "region" or "entities" or "poi" or "data" or "playerdata" or "players") continue;
            if (!HasLevel(child) && !HasStructure(root, child, token)) continue;
            candidates.Add(WorldPaths.RequireContained(root, child));
            if (candidates.Count > 1) return (null, true);
        }
        return (candidates.SingleOrDefault(), false);
    }

    private static bool HasLevel(string path) => File.Exists(Path.Combine(path, "level.dat"));
    private static bool HasStructure(string source, string path, CancellationToken token)
    {
        foreach (var relative in new[] { "region", Path.Combine("DIM-1", "region"), Path.Combine("DIM1", "region") })
        {
            var candidate = Path.Combine(path, relative);
            if (!Directory.Exists(candidate)) continue;
            WorldPaths.RequireContained(source, candidate);
            return true;
        }
        var dimensions = Path.Combine(path, "dimensions");
        if (!Directory.Exists(dimensions)) return false;
        WorldPaths.RequireContained(source, dimensions);
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((dimensions, 0));
        var visited = 0;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 4096 || item.Depth > 16) throw new IOException("Partial world layout exceeds preview limits.");
            foreach (var child in Directory.EnumerateDirectories(item.Path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (Path.GetFileName(child) == "region") return true;
                if (Path.GetFileName(child) is not ("entities" or "poi" or "data")) pending.Push((child, item.Depth + 1));
            }
        }
        return false;
    }
}
