using System.Collections.ObjectModel;

namespace MineRewind.World;

public enum WorldLayoutFamily { Legacy, Namespaced }
public sealed record WorldLayoutIssue(string Code, string RelativePath, string? DimensionId = null);
public sealed record WorldRegionFile(ChunkComponentKind Component, RegionKey Region, string RelativePath);
public sealed record DimensionLayoutFacts(string Id, string RelativeRoot, WorldLayoutFamily Family,
    IReadOnlyDictionary<ChunkComponentKind, string> ComponentDirectories, IReadOnlyList<WorldRegionFile> RegionFiles);
/// <summary>All paths and identities are Source-relative, whether observed on disk or from a fixed inventory.</summary>
public sealed record WorldLayoutFacts(string WorldId, IReadOnlyList<DimensionLayoutFacts> Dimensions,
    IReadOnlyList<WorldLayoutIssue> Issues, bool IsComplete)
{
    public WorldIdentityEvidence IdentityEvidence { get; init; }
}

/// <summary>Bounded layout facts. Callers retain their distinct complete/partial/scope acceptance policy.</summary>
public static class WorldLayoutReader
{
    public static WorldLayoutFacts Read(string sourceRoot, string worldRoot, bool rejectLinks = true, CancellationToken token = default)
        => Read(sourceRoot, worldRoot, rejectLinks, new WorldScanBudget(), token);

    internal static WorldLayoutFacts Read(string sourceRoot, string worldRoot, bool rejectLinks, WorldScanBudget budget, CancellationToken token)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        // An explicitly selected Source alias is authorized by the Host binding. Resolve only
        // that root; links discovered below it remain forbidden. Returned paths stay Source-relative.
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
        {
            var relativeWorld = Path.GetRelativePath(sourceRoot, Path.GetFullPath(worldRoot));
            if (Path.IsPathRooted(relativeWorld) || relativeWorld == ".." || relativeWorld.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IOException("World path is outside the configured source.");
            sourceRoot = new DirectoryInfo(sourceRoot).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new IOException("Configured Source alias cannot be resolved.");
            worldRoot = Path.GetFullPath(Path.Combine(sourceRoot, relativeWorld));
        }
        worldRoot = WorldPaths.RequireContained(sourceRoot, worldRoot);
        var candidates = new Dictionary<(string Id, string Path), WorldLayoutFamily>();
        var issues = new List<WorldLayoutIssue>();
        var namespacedOverworld = Path.Combine(worldRoot, "dimensions", "minecraft", "overworld");
        if (HasComponents(worldRoot) || !Directory.Exists(namespacedOverworld)) Add(MinecraftPathGrammar.Overworld, worldRoot, WorldLayoutFamily.Legacy);
        Add(MinecraftPathGrammar.Nether, Path.Combine(worldRoot, "DIM-1"), WorldLayoutFamily.Legacy);
        Add(MinecraftPathGrammar.End, Path.Combine(worldRoot, "DIM1"), WorldLayoutFamily.Legacy);
        var parent = Path.GetDirectoryName(worldRoot)!; var name = Path.GetFileName(worldRoot);
        Add(MinecraftPathGrammar.Nether, Path.Combine(parent, name + "_nether", "DIM-1"), WorldLayoutFamily.Legacy);
        Add(MinecraftPathGrammar.End, Path.Combine(parent, name + "_the_end", "DIM1"), WorldLayoutFamily.Legacy);
        var dimensions = Path.Combine(worldRoot, "dimensions");
        if (Directory.Exists(dimensions))
        {
            foreach (var ns in Entries(dimensions).Where(Directory.Exists))
            {
                var pending = new Stack<(string Path, int Depth)>(); pending.Push((ns, 0));
                while (pending.TryPop(out var item))
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Depth > 16) throw new WorldScanLimitException("Dimension layout exceeds the depth limit.");
                    if (item.Depth > 0 && (HasComponents(item.Path) || Path.GetFileName(ns) == "minecraft"
                        && Path.GetFileName(item.Path) is "overworld" or "the_nether" or "the_end"))
                        Add(Path.GetFileName(ns) + ":" + Path.GetRelativePath(ns, item.Path).Replace('\\', '/'), item.Path, WorldLayoutFamily.Namespaced);
                    foreach (var child in Entries(item.Path, item.Depth + 1).Where(Directory.Exists))
                    {
                        if (Path.GetFileName(child) is "region" or "entities" or "poi" or "data") continue;
                        if (item.Depth + 1 > 16) throw new WorldScanLimitException("Dimension layout exceeds the depth limit.");
                        pending.Push((child, item.Depth + 1));
                    }
                }
            }
        }
        var result = new List<DimensionLayoutFacts>();
        foreach (var (candidate, family) in candidates.OrderBy(p => p.Key.Id, StringComparer.Ordinal).ThenBy(p => p.Key.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var components = new Dictionary<ChunkComponentKind, string>(); var files = new List<WorldRegionFile>();
            for (var index = 0; index < MinecraftPathGrammar.Components.Count; index++)
            {
                var component = (ChunkComponentKind)index;
                var directory = Path.Combine(candidate.Path, MinecraftPathGrammar.Components[index]);
                if (!Directory.Exists(directory)) continue;
                try { WorldPaths.RequireContained(sourceRoot, directory); }
                catch (IOException) when (!rejectLinks) { budget.MarkIncomplete(); issues.Add(new("linked_component", Relative(directory), candidate.Id)); continue; }
                components.Add(component, Relative(directory));
                foreach (var file in Entries(directory))
                    if (!Directory.Exists(file) && MinecraftPathGrammar.TryRegionName(file, out var region)) files.Add(new(component, region, Relative(file)));
            }
            result.Add(new(candidate.Id, Relative(candidate.Path), family,
                new ReadOnlyDictionary<ChunkComponentKind, string>(components), files.AsReadOnly()));
        }
        foreach (var duplicate in result.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            issues.Add(new("dimension_layout_ambiguous", Relative(worldRoot), duplicate.Key));
        if (budget.Incomplete) issues.Add(new("layout_read_incomplete", Relative(worldRoot)));
        var evidence = (File.Exists(Path.Combine(worldRoot, "level.dat")) ? WorldIdentityEvidence.PrimaryMetadata : 0)
            | (File.Exists(Path.Combine(worldRoot, "level.dat_old")) ? WorldIdentityEvidence.BackupMetadata : 0)
            | (File.Exists(Path.Combine(worldRoot, "session.lock")) ? WorldIdentityEvidence.SessionLock : 0)
            | (result.Any(d => d.ComponentDirectories.Count != 0) ? WorldIdentityEvidence.WorldLayout : 0);
        return new(Relative(worldRoot), result.AsReadOnly(), issues.AsReadOnly(), !budget.Incomplete) { IdentityEvidence = evidence };

        string Relative(string path) => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        IEnumerable<string> Entries(string directory, int depth = 0)
        {
            using var iterator = budget.Entries(sourceRoot, directory, token, rejectLinks, depth).GetEnumerator();
            while (true)
            {
                bool next;
                try { next = iterator.MoveNext(); }
                catch (WorldScanLimitException) when (!rejectLinks) { yield break; }
                if (!next) yield break;
                yield return iterator.Current;
            }
        }
        void Add(string id, string path, WorldLayoutFamily family)
        {
            if (!Directory.Exists(path)) return;
            var relative = Relative(path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            { issues.Add(new("dimension_outside_source", relative, id)); return; }
            try { path = WorldPaths.RequireContained(sourceRoot, path); }
            catch (IOException) when (!rejectLinks) { budget.MarkIncomplete(); issues.Add(new("linked_dimension", relative, id)); return; }
            if (!candidates.Keys.Any(key => key.Id == id && key.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) candidates.Add((id, path), family);
        }
    }

    public static IReadOnlyList<WorldLayoutFacts> FromInventory(IEnumerable<string> relativePaths)
    {
        var paths = new List<string>(); long characters = 0;
        foreach (var path in relativePaths)
        {
            if (paths.Count == Semantic.SemanticPaths.MaximumInventoryEntries
                || (characters += path.Length) > Semantic.SemanticPaths.MaximumInventoryPathCharacters)
                throw new IOException("Fixed layout inventory exceeds its metadata budget.");
            paths.Add(Semantic.SemanticPaths.Normalize(path));
        }
        var worlds = MinecraftPathGrammar.WorldRoots(paths);
        var addressed = paths.Select(path => (Path: path, Address: MinecraftPathGrammar.TryRegionAddress(path, worlds, out var address) ? address : null))
            .Where(item => item.Address is not null).ToArray();
        return addressed.GroupBy(item => item.Address!.WorldId, StringComparer.Ordinal).Select(world =>
        {
            var dimensions = world.GroupBy(item => (item.Address!.DimensionId, Root: Parent(Parent(item.Path)))).Select(group =>
            {
                var components = group.GroupBy(item => item.Address!.Component).ToDictionary(g => g.Key, g => Parent(g.First().Path));
                var relative = group.Key.Root;
                var local = world.Key == "." ? relative : relative == world.Key ? "" : relative[(world.Key.Length + 1)..];
                MinecraftPathGrammar.TryDimension(local, out _, out var family);
                return new DimensionLayoutFacts(group.Key.DimensionId, relative.Length == 0 ? "." : relative, family,
                    new ReadOnlyDictionary<ChunkComponentKind, string>(components), Array.AsReadOnly(group.Select(item =>
                        new WorldRegionFile(item.Address!.Component, new(item.Address.X, item.Address.Z), item.Path)).ToArray()));
            }).ToArray();
            var issues = dimensions.GroupBy(d => d.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)
                .Select(g => new WorldLayoutIssue("dimension_layout_ambiguous", world.Key, g.Key)).ToArray();
            var prefix = world.Key == "." ? "" : world.Key + "/";
            return new WorldLayoutFacts(world.Key, Array.AsReadOnly(dimensions), Array.AsReadOnly(issues), true)
            {
                IdentityEvidence = WorldIdentityEvidence.WorldLayout
                    | (paths.Contains(prefix + "level.dat", StringComparer.Ordinal) ? WorldIdentityEvidence.PrimaryMetadata : 0)
                    | (paths.Contains(prefix + "level.dat_old", StringComparer.Ordinal) ? WorldIdentityEvidence.BackupMetadata : 0)
                    | (paths.Contains(prefix + "session.lock", StringComparer.Ordinal) ? WorldIdentityEvidence.SessionLock : 0)
            };
        }).OrderBy(w => w.WorldId, StringComparer.Ordinal).ToArray();
        static string Parent(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";
    }

    private static bool HasComponents(string path) => MinecraftPathGrammar.Components.Any(component => Directory.Exists(Path.Combine(path, component)));
}
