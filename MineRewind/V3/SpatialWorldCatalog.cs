using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

internal static class SpatialWorldCatalog
{
    internal static string Root(SpatialPreviewSource source) => Path.GetFullPath(source.FixedInput?.RootPath ?? source.Folder.Path);
    internal static string[] Inventory(string root, CancellationToken token)
        => Inventory(root, token, true, out _);
    internal static string[] Inventory(string root, CancellationToken token, bool rejectLinks, out bool incomplete, WorldScanBudget? scanBudget = null)
    {
        var result = new List<string>();
        var budget = scanBudget ?? new WorldScanBudget();
        foreach (var entry in budget.Files(root, root, token, rejectLinks))
            result.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
        incomplete = budget.Incomplete;
        return result.Order(StringComparer.Ordinal).ToArray();
    }
    internal static string[] Worlds(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        var worlds = MinecraftPathGrammar.WorldRoots(values);
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
