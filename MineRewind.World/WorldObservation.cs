namespace MineRewind.World;

public enum WorldObservationKind { Ordinary, ResolvedWorlds, WorldEvidenceUnresolved }
[Flags]
public enum WorldIdentityEvidence { None = 0, PrimaryMetadata = 1, BackupMetadata = 2, SessionLock = 4, WorldLayout = 8, ServerConfiguration = 16 }
public sealed record ObservedWorld(string Path, bool HasPrimaryMetadata)
{
    public WorldIdentityEvidence Evidence { get; init; }
}
public sealed record WorldObservationIssue(string Path, string Code)
{
    public string? ExceptionType { get; init; }
    public int? HResult { get; init; }
}
public sealed record WorldObservation(WorldObservationKind Kind, IReadOnlyList<ObservedWorld> Worlds,
    IReadOnlyList<WorldObservationIssue> Issues)
{
    public bool IsComplete => Kind != WorldObservationKind.WorldEvidenceUnresolved;
}

/// <summary>Bounded identity observation, not NBT validation and not a filesystem snapshot.</summary>
public static class WorldObserver
{
    private static readonly HashSet<string> WorldDataDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "region", "entities", "poi", "dimensions", "DIM-1", "DIM1", "playerdata", "players", "advancements", "stats", "datapacks", "data" };

    public static WorldObservation Observe(string source, CancellationToken token = default)
        => ObserveCore(source, null, token);
    internal static WorldObservation ObserveForDisplay(string source, WorldScanBudget budget, CancellationToken token)
        => ObserveCore(source, budget, token);
    private static WorldObservation ObserveCore(string source, WorldScanBudget? budget, CancellationToken token)
    {
        var worlds = new List<ObservedWorld>();
        var issues = new List<WorldObservationIssue>();
        var configuredWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Path, int Depth)>();
        var incomplete = false;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        pending.Push((root, 0));
        var directories = 0; var entries = 0;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++directories > 4096 || item.Depth > 16)
            { Issue(item.Path, "observation_budget"); break; }
            try
            {
                var attributes = File.GetAttributes(item.Path);
                if ((attributes & FileAttributes.Directory) == 0) { Issue(item.Path, "source_not_directory"); continue; }
                // An explicitly configured root alias is supported; aliases discovered below it are never followed.
                if ((attributes & FileAttributes.ReparsePoint) != 0 && item.Path != root) { Issue(item.Path, "linked_world_path"); continue; }
                var primary = HasFile(item.Path, "level.dat");
                var old = HasFile(item.Path, "level.dat_old");
                var hasLock = HasFile(item.Path, "session.lock");
                var structure = new[] { "region", "entities", "poi", "dimensions", "DIM-1", "DIM1" }
                    .Any(name => Directory.Exists(Path.Combine(item.Path, name)));
                var isWorld = primary || old || hasLock && structure;
                if (isWorld)
                {
                    worlds.Add(new(item.Path, primary) { Evidence = (primary ? WorldIdentityEvidence.PrimaryMetadata : 0)
                        | (old ? WorldIdentityEvidence.BackupMetadata : 0) | (hasLock ? WorldIdentityEvidence.SessionLock : 0)
                        | (structure ? WorldIdentityEvidence.WorldLayout : 0) });
                    if (!primary) issues.Add(new(item.Path, "primary_metadata_missing"));
                }
                // A same-named business file is not sufficient world evidence without markers/layout.

                if (HasFile(item.Path, "server.properties"))
                {
                    var properties = Path.Combine(item.Path, "server.properties");
                    if (new FileInfo(properties).Length > 65536) Issue(properties, "server_properties_budget");
                    else foreach (var line in WorldLocator.ReadProperties(properties, token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!MinecraftPathGrammar.TryLevelName(line, out var name)) continue;
                        name ??= "world";
                        var child = Path.GetFullPath(Path.Combine(item.Path, name));
                        var relative = Path.GetRelativePath(root, child);
                        if (Path.IsPathRooted(name) || Path.IsPathRooted(relative) || relative == ".."
                            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            Issue(properties, "server_world_outside_source");
                        else if (!Directory.Exists(child)) Issue(child, "configured_world_missing");
                        else configuredWorlds.Add(child);
                        break;
                    }
                }
                foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    token.ThrowIfCancellationRequested();
                    budget?.Count(root, entry, token, item.Depth + 1);
                    if (++entries > 100_000) { Issue(item.Path, "observation_budget"); pending.Clear(); break; }
                    var flags = File.GetAttributes(entry);
                    if ((flags & FileAttributes.Directory) == 0) continue;
                    // Host capture excludes linked entries. Never follow them to infer a world outside the Source.
                    if ((flags & FileAttributes.ReparsePoint) != 0) { budget?.MarkIncomplete(); continue; }
                    if (isWorld && WorldDataDirectories.Contains(Path.GetFileName(entry))) continue;
                    pending.Push((entry, item.Depth + 1));
                }
            }
            catch (WorldScanLimitException) { Issue(item.Path, "observation_budget"); pending.Clear(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Issue(item.Path, "world_observation_failed", ex); }
        }
        foreach (var configured in configuredWorlds)
            if (!worlds.Any(w => StringComparer.OrdinalIgnoreCase.Equals(w.Path, configured))) Issue(configured, "world_identity_unresolved");
        for (var index = 0; index < worlds.Count; index++)
            if (configuredWorlds.Contains(worlds[index].Path)) worlds[index] = worlds[index] with
                { Evidence = worlds[index].Evidence | WorldIdentityEvidence.ServerConfiguration };
        return new(incomplete ? WorldObservationKind.WorldEvidenceUnresolved
            : worlds.Count > 0 ? WorldObservationKind.ResolvedWorlds : WorldObservationKind.Ordinary,
            worlds.OrderBy(w => w.Path, StringComparer.OrdinalIgnoreCase).ToArray(), issues.ToArray());

        void Issue(string path, string code, Exception? error = null)
        {
            incomplete = true;
            if (issues.Count < 32) issues.Add(new(path, code) { ExceptionType = error?.GetType().FullName, HResult = error?.HResult });
        }
        static bool HasFile(string directory, string name)
        {
            try
            {
                var attributes = File.GetAttributes(Path.Combine(directory, name));
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metadata.");
                return (attributes & FileAttributes.Directory) == 0;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
    }
}
