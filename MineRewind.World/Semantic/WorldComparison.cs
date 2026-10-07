namespace MineRewind.World.Semantic;

public enum ComparisonChoice { Equal, Ours, Theirs, Delete, Conflict, Blocked, DimensionReview }
public sealed record BundleDecision(string SourceId, string WorldId, string DimensionId, int X, int Z,
    ComparisonChoice Choice, bool ContainerChanged, bool TimestampChanged, bool EncodingChanged, string? Diagnostic);
public sealed record FileDecision(string RelativePath, ComparisonChoice Choice, bool ContainerOnly);
public sealed record ComparisonSummary(int Subjects, int Conflicts, int Blocked, int DimensionReviews, int FileChanges,
    bool Experimental = true, bool Applicable = false);

/// <summary>Diagnostic only: no resolution workspace, executable plan or merge output.</summary>
public static class WorldComparison
{
    public static ComparisonSummary Compare(ISemanticScratch @base, ISemanticScratch ours, ISemanticScratch theirs,
        Action<BundleDecision> emit, Action<FileDecision>? emitFile = null, CancellationToken token = default)
    {
        var stores = new[] { @base, ours, theirs };
        var completions = stores.Select(s => ScratchArchive.Validate(s, token)).ToArray();
        foreach (var store in stores) ScratchArchive.VerifyIntegrity(store, token);
        if (completions.Any(c => c.SourceId != completions[0].SourceId)) throw new InvalidDataException("Source identities differ.");
        var unknownInventory = completions.Any(c => c.Summary.UnclassifiedRegions != 0);
        var entityChanges = new Dictionary<string, (bool Ours, bool Theirs)>(StringComparer.Ordinal);
        var ownedContainerPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups(stores, token))
        {
            foreach (var region in group.SelectMany(g => g))
            {
                ownedContainerPaths.Add(region.RelativePath);
                foreach (var chunk in region.Chunks)
                    if (chunk.State == SemanticState.Present && chunk.ExternalPath is { } path) ownedContainerPaths.Add(path);
            }
            var entity = group.Select(g => g.FirstOrDefault(r => r.Address.Component == ChunkComponentKind.Entities)).ToArray();
            var key = group.SelectMany(g => g).First().Address.DimensionKey;
            entityChanges.TryGetValue(key, out var changed);
            entityChanges[key] = (changed.Ours || !RegionContentEqual(entity[0], entity[1]),
                changed.Theirs || !RegionContentEqual(entity[0], entity[2]));
        }
        int subjects = 0, conflicts = 0, blocked = 0, reviews = 0;
        foreach (var group in Groups(stores, token))
        {
            var address = group.SelectMany(g => g).First().Address;
            var invalid = unknownInventory || group.SelectMany(g => g).Any(r => r.State != SemanticState.Present);
            var slots = group.SelectMany(g => g).SelectMany(r => r.Chunks).Where(c => c.State != SemanticState.Absent)
                .Select(c => c.Slot).Distinct().Order().ToArray();
            // A malformed header has no trustworthy slots. Emit a blocked region sentinel, never absence.
            if (slots.Length == 0 && invalid) slots = [-1];
            var maps = group.Select(g => g.ToDictionary(r => r.Address.Component)).ToArray();
            foreach (var slot in slots)
            {
                token.ThrowIfCancellationRequested();
                var values = maps.Select(m => Bundle(m, slot)).ToArray();
                var choice = invalid || values.Any(v => v.Unknown) ? ComparisonChoice.Blocked
                    : Pick(values[0].Value, values[1].Value, values[2].Value);
                string? diagnostic = choice == ComparisonChoice.Blocked ? "unknown_input_not_absence" : null;
                if (choice != ComparisonChoice.Blocked && entityChanges[address.DimensionKey] is (true, true))
                { choice = ComparisonChoice.DimensionReview; diagnostic = "both_sides_changed_entities_in_dimension"; }
                bool timestamp = false, encoding = false;
                foreach (var component in Enum.GetValues<ChunkComponentKind>())
                {
                    var chunks = maps.Select(m => GetChunk(m, component, slot)).ToArray();
                    timestamp |= chunks.Select(c => c?.Timestamp).Distinct().Count() > 1;
                    encoding |= chunks.Select(c => (c?.Compression, c?.External)).Distinct().Count() > 1;
                }
                var container = maps[0].Count != maps[1].Count || maps[0].Count != maps[2].Count
                    || Enum.GetValues<ChunkComponentKind>().Any(k => maps.Select(m => m.GetValueOrDefault(k)?.FileHash).Distinct().Count() > 1);
                emit(new(completions[0].SourceId, address.WorldId, address.DimensionId,
                    checked(address.X * 32 + (slot < 0 ? 0 : slot % 32)), checked(address.Z * 32 + (slot < 0 ? 0 : slot / 32)),
                    choice, container, timestamp, encoding, slot < 0 ? "unknown_region_extent" : diagnostic));
                subjects++;
                if (choice == ComparisonChoice.Conflict) conflicts++;
                if (choice == ComparisonChoice.Blocked) blocked++;
                if (choice == ComparisonChoice.DimensionReview) reviews++;
            }
        }
        var fileResult = CompareFiles(stores, ownedContainerPaths, emitFile, token);
        // Unclassified region paths are also emitted as blocked file decisions by CompareFiles.
        blocked += completions.Any(c => c.Summary.UnclassifiedRegions != 0) ? 1 : 0;
        return new(subjects, conflicts + fileResult.Conflicts, blocked + fileResult.Blocked, reviews, fileResult.Changes);
    }

    private static (string? Value, bool Unknown) Bundle(Dictionary<ChunkComponentKind, RegionEntry> map, int slot)
    {
        var values = Enum.GetValues<ChunkComponentKind>().Select(k => GetChunk(map, k, slot)).ToArray();
        if (values.Any(c => c is not null && c.State is not (SemanticState.Present or SemanticState.Absent))) return (null, true);
        return values.All(c => c is null || c.State == SemanticState.Absent) ? (null, false)
            : (string.Join('|', values.Select(c => c is null || c.State == SemanticState.Absent ? "absent" : c.PayloadHash)), false);
    }
    private static ChunkEntry? GetChunk(Dictionary<ChunkComponentKind, RegionEntry> map, ChunkComponentKind component, int slot)
        => slot >= 0 && map.TryGetValue(component, out var region) && region.Chunks.Count == 1024 ? region.Chunks[slot] : null;
    private static bool RegionContentEqual(RegionEntry? a, RegionEntry? b)
    {
        if (a?.State is not (null or SemanticState.Present) || b?.State is not (null or SemanticState.Present)) return false;
        var left = a?.Chunks.Where(c => c.State != SemanticState.Absent).Select(c => (c.Slot, c.State, c.PayloadHash)) ?? [];
        var right = b?.Chunks.Where(c => c.State != SemanticState.Absent).Select(c => (c.Slot, c.State, c.PayloadHash)) ?? [];
        return left.SequenceEqual(right);
    }
    private static ComparisonChoice Pick(string? b, string? o, string? t)
        => o == t ? o is null ? ComparisonChoice.Delete : ComparisonChoice.Equal
            : o == b ? t is null ? ComparisonChoice.Delete : ComparisonChoice.Theirs
            : t == b ? o is null ? ComparisonChoice.Delete : ComparisonChoice.Ours : ComparisonChoice.Conflict;

    private static IEnumerable<RegionEntry[][]> Groups(ISemanticScratch[] stores, CancellationToken token)
    {
        var cursors = stores.Select(s => ScratchArchive.ReadLines<RegionEntry>(s, "regions.jsonl", token).GetEnumerator()).ToArray();
        try
        {
            var live = cursors.Select(c => c.MoveNext()).ToArray();
            var previous = new string?[3];
            while (live.Any(v => v))
            {
                var key = Enumerable.Range(0, 3).Where(i => live[i]).Select(i => cursors[i].Current.Address.GroupKey).Min(StringComparer.Ordinal)!;
                var groups = new RegionEntry[3][];
                for (var i = 0; i < 3; i++)
                {
                    var entries = new List<RegionEntry>();
                    while (live[i] && cursors[i].Current.Address.GroupKey == key)
                    {
                        var row = cursors[i].Current;
                        if (!Enum.IsDefined(row.Address.Component) || row.Chunks.Count is not (0 or 1024)
                            || row.State == SemanticState.Present && row.Chunks.Count != 1024
                            || row.Chunks.Where((c, slot) => c.Slot != slot || c.X != row.Address.X * 32 + slot % 32
                                || c.Z != row.Address.Z * 32 + slot / 32
                                || c.State == SemanticState.Present && string.IsNullOrEmpty(c.PayloadHash)).Any())
                            throw new InvalidDataException("Invalid semantic index.");
                        if (previous[i] is { } old && StringComparer.Ordinal.Compare(old, row.Address.SortKey) >= 0)
                            throw new InvalidDataException("Region index is unordered or ambiguous.");
                        previous[i] = row.Address.SortKey;
                        entries.Add(row); live[i] = cursors[i].MoveNext();
                    }
                    groups[i] = entries.ToArray();
                }
                yield return groups;
            }
        }
        finally { foreach (var cursor in cursors) cursor.Dispose(); }
    }

    private static (int Changes, int Conflicts, int Blocked) CompareFiles(ISemanticScratch[] stores,
        IReadOnlySet<string> ownedContainerPaths, Action<FileDecision>? emit, CancellationToken token)
    {
        var cursors = stores.Select(s => ScratchArchive.ReadLines<StoredFile>(s, "files.jsonl", token).GetEnumerator()).ToArray();
        try
        {
            var live = cursors.Select(c => c.MoveNext()).ToArray(); int changes = 0, conflicts = 0, blocked = 0;
            while (live.Any(v => v))
            {
                token.ThrowIfCancellationRequested();
                var path = Enumerable.Range(0, 3).Where(i => live[i]).Select(i => cursors[i].Current.RelativePath).Min(StringComparer.Ordinal)!;
                var files = new StoredFile?[3];
                for (var i = 0; i < 3; i++)
                    if (live[i] && cursors[i].Current.RelativePath == path) { files[i] = cursors[i].Current; live[i] = cursors[i].MoveNext(); }
                if (files.Select(f => f?.Sha256).Distinct().Count() == 1) continue;
                var choice = Pick(files[0]?.Sha256, files[1]?.Sha256, files[2]?.Sha256);
                if (path.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)
                    && files.Any(f => f is { Encoding: FileEncoding.Raw or FileEncoding.OpaqueRegion })) choice = ComparisonChoice.Blocked;
                var containerOnly = ownedContainerPaths.Contains(path);
                emit?.Invoke(new(path, choice, containerOnly)); changes++;
                // Region and referenced mcc byte differences do not override their bundle's semantic decision.
                if (!containerOnly)
                {
                    if (choice == ComparisonChoice.Conflict) conflicts++;
                    if (choice == ComparisonChoice.Blocked) blocked++;
                }
            }
            return (changes, conflicts, blocked);
        }
        finally { foreach (var cursor in cursors) cursor.Dispose(); }
    }
}
