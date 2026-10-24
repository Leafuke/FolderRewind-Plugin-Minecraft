using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

internal static class SpatialWorldCatalog
{
    internal static string Root(SpatialPreviewSource source) => Path.GetFullPath(source.FixedInput?.RootPath ?? source.Folder.Path);
    internal static string[] Inventory(string root, CancellationToken token)
    {
        var result = new List<string>();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            WorldPaths.RequireContained(root, item.Path);
            if (item.Depth > 64) throw new IOException("Spatial input exceeds the directory depth budget.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Spatial input contains a linked path.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, item.Depth + 1));
                else
                {
                    if (result.Count >= 100_000) throw new IOException("Spatial input exceeds the file budget.");
                    result.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                }
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    internal static string[] Worlds(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        var worlds = values.Where(p => p == "level.dat" || p.EndsWith("/level.dat", StringComparison.Ordinal))
            .Select(p => p[..^9].TrimEnd('/')).Distinct(StringComparer.Ordinal).OrderByDescending(p => p.Length).ToArray();
        if (worlds.Length == 0 && values.Any(p => MineRewind.World.Semantic.MergeRegion.TryAddress(p, [""], out _))) return [""];
        return worlds;
    }
    internal static string PathFor(string root, string id)
    {
        if (id == ".") return root;
        if (Path.IsPathFullyQualified(id) || id.Split('/').Any(s => s is "" or "." or "..")) throw new InvalidDataException("Invalid spatial world identity.");
        return WorldPaths.RequireContained(root, Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar)));
    }
}
