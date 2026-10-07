using System.Security.Cryptography;
using System.Text.Json;

namespace MineRewind.World.Semantic;

/// <summary>Complete file directory with immutable recipes. No previous state-root dependency.</summary>
public static class WorldIncrementalFormat
{
    public const string Format = "minecraft-world-incremental";
    public const string RootPath = "world-state.json";
    public const int ResidualBlockBytes = 65536;
    public const long DataPackBytes = 64L * 1024 * 1024;
    public const long RecipePackBytes = 8L * 1024 * 1024;

    public static WorldIncrementalState Capture(IReadOnlyWorldSource changedSource, WorldIncrementalState? baseline,
        IWorldObjectReader reader, IWorldObjectWriter writer, string sourceId, string boundary,
        IReadOnlyList<string> added, IReadOnlyList<string> modified, IReadOnlyList<string> deleted,
        bool checkpoint, CancellationToken token)
    {
        if (baseline is not null && (baseline.Version != 1 || baseline.SourceId != sourceId || baseline.BoundaryFingerprint != boundary))
            throw new InvalidDataException("Incompatible incremental baseline.");
        checkpoint |= baseline?.CompactionRecommended == true;
        var prior = baseline?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal) ?? [];
        var current = new Dictionary<string, WorldStateFile>(prior, StringComparer.Ordinal);
        var inventory = changedSource.GetInventory(token).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var updates = added.Concat(modified).ToHashSet(StringComparer.Ordinal);
        if (updates.Count != added.Count + modified.Count || deleted.Distinct(StringComparer.Ordinal).Count() != deleted.Count
            || updates.Overlaps(deleted) || !updates.SetEquals(inventory.Keys))
            throw new InvalidDataException("Physical input differs from the declared change set.");
        foreach (var path in added) if (prior.ContainsKey(path)) throw new InvalidDataException("Added path already exists.");
        foreach (var path in modified.Concat(deleted)) if (!prior.ContainsKey(path)) throw new InvalidDataException("Changed path has no baseline.");
        foreach (var path in deleted) current.Remove(SemanticPaths.Normalize(path));
        var allPaths = current.Keys.Concat(updates).ToHashSet(StringComparer.Ordinal);
        if (allPaths.Count > SemanticPaths.MaximumInventoryEntries || allPaths.Sum(p => (long)p.Length) > SemanticPaths.MaximumInventoryPathCharacters
            || allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allPaths.Count)
            throw new InvalidDataException("Incremental directory quota or path collision.");
        var worlds = allPaths.Where(p => Path.GetFileName(p) == "level.dat")
            .Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : "").OrderByDescending(p => p.Length).ToArray();
        var source = new OverlaySource(changedSource, inventory, prior, deleted.ToHashSet(StringComparer.Ordinal), reader, token);
        var deletedChunks = new List<string>(); long newBytes = 0;

        foreach (var path in updates.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            SemanticPaths.Normalize(path);
            var info = inventory[path];
            if (info.Length < 0 || info.Length > 16L * 1024 * 1024 * 1024) throw new IOException("File quota exceeded.");
            using var input = source.OpenRead(path);
            var hash = SemanticStreams.Hash(input, token); input.Position = 0;
            prior.TryGetValue(path, out var old);
            WorldFileRecipe? oldRecipe = old is null ? null : Read<WorldFileRecipe>(reader, old.Recipe, token);
            RegionEntry? oldIndex = old?.Index is null ? null : Read<RegionEntry>(reader, old.Index, token);
            RegionEntry? index = null; IReadOnlyList<PayloadSlice> payloads = [];
            var encoding = "Raw";
            if (WorldCapture.TryAddress(path, worlds, out var address))
            {
                var analyzed = RegionCodec.Analyze(source, info, input, address!, allPaths, token, oldIndex);
                index = analyzed.Entry with { FileHash = hash };
                if (index.Chunks.Any(c => c.Diagnostic is "payload_unavailable" or "decode_limit")) throw new IOException("Captured payload is unavailable or exceeds the decoding budget.");
                if (index.State == SemanticState.Present) { encoding = "SegmentedRegion"; payloads = analyzed.Payloads; }
                else encoding = "OpaqueRegion";
                if (oldIndex is not null && index.State == SemanticState.Present)
                    foreach (var chunk in index.Chunks.Where(c => c.State == SemanticState.Absent))
                        if (oldIndex.Chunks.Any(c => c.Slot == chunk.Slot && c.State == SemanticState.Present))
                            deletedChunks.Add(path + "#" + chunk.Slot);
            }
            var segments = new List<WorldRecipeSegment>();
            var reusable = encoding == "Raw" ? new Dictionary<string, WorldObjectReference>()
                : (oldRecipe?.Segments ?? []).GroupBy(s => s.Content.Length + ":" + s.Content.Sha256)
                    .ToDictionary(g => g.Key, g => g.First().Content);
            long offset = 0;
            foreach (var payload in payloads.OrderBy(p => p.Offset))
            {
                Residual(offset, payload.Offset);
                Store(payload.Offset, payload.Length);
                offset = payload.Offset + payload.Length;
            }
            Residual(offset, info.Length);
            if (input.Length != info.Length) throw new IOException("Captured file length changed.");
            var recipe = new WorldFileRecipe(1, encoding, info.Length, segments);
            var recipeRef = Write(writer, recipe, segments.Select(s => s.Content.ArtifactId).Distinct().ToArray(), token);
            var indexRef = index is null ? null : Write(writer, index, [], token);
            current[path] = new(path, info.Length, hash, recipeRef, indexRef);

            void Residual(long start, long end)
            {
                while (start < end)
                {
                    var block = encoding == "Raw" ? DataPackBytes : ResidualBlockBytes - start % ResidualBlockBytes;
                    var length = Math.Min(block, end - start); Store(start, length); start += length;
                }
            }
            void Store(long start, long length)
            {
                if (length <= 0) return;
                input.Position = start;
                var digest = HashRange(input, length, token);
                if (!checkpoint && reusable.TryGetValue(length + ":" + digest, out var existing))
                    segments.Add(new(start, existing));
                else
                {
                    input.Position = start;
                    segments.Add(new(start, writer.Store(input, length, false, [], token))); newBytes += length;
                }
            }
        }

        // An external file can change without changing the owner's .mca bytes or recipe.
        foreach (var owner in updates.Concat(deleted).Select(ExternalOwner).Where(p => p is not null).Distinct())
        {
            if (updates.Contains(owner!) || !current.TryGetValue(owner!, out var file) || file.Index is null) continue;
            var oldIndex = Read<RegionEntry>(reader, file.Index, token);
            if (oldIndex.Chunks.Count == 0) continue; // opaque owner: never invent external references
            var chunks = oldIndex.Chunks.Select(chunk => RefreshExternal(chunk)).ToArray();
            var issues = chunks.Where(c => c.Diagnostic is not null).Select(c => "slot:" + c.Slot + ":" + c.Diagnostic).ToArray();
            var state = chunks.FirstOrDefault(c => c.State is not (SemanticState.Present or SemanticState.Absent))?.State ?? SemanticState.Present;
            var refreshed = oldIndex with { Chunks = chunks, State = state, Diagnostics = issues };
            current[owner!] = file with { Index = Write(writer, refreshed, [], token) };
        }

        // Decide from actual new bytes, not the size of the changed region files.
        var logicalBytes = current.Values.Sum(f => f.Length);
        checkpoint |= logicalBytes > 0 && newBytes >= logicalBytes * 0.70;
        // Keep this transaction's payloads; copy only inherited objects, never recapture them.
        if (checkpoint)
        {
            writer.BeginRepack();
            var combined = new CombinedObjects(reader, writer);
            foreach (var path in current.Keys.Order(StringComparer.Ordinal).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var file = current[path]; var recipe = Read<WorldFileRecipe>(combined, file.Recipe, token);
                var segments = new List<WorldRecipeSegment>();
                foreach (var segment in recipe.Segments)
                {
                    if (writer.Owns(segment.Content.ArtifactId)) { segments.Add(segment); continue; }
                    using var bytes = reader.Open(segment.Content, token);
                    segments.Add(new(segment.FileOffset, writer.Store(bytes, segment.Content.Length, false, [], token)));
                    newBytes += segment.Content.Length;
                }
                var recipeRef = Write(writer, recipe with { Segments = segments }, segments.Select(s => s.Content.ArtifactId).Distinct().ToArray(), token);
                var indexRef = file.Index is not null ? Write(writer, Read<RegionEntry>(combined, file.Index, token), [], token) : null;
                current[path] = file with { Recipe = recipeRef, Index = indexRef };
            }
        }
        if (current.Values.Sum(f => f.Length) > 16L * 1024 * 1024 * 1024) throw new IOException("World byte quota exceeded.");
        var accountingReader = new CombinedObjects(reader, writer);
        var ranges = new Dictionary<Guid, HashSet<(long Start, long End)>>();
        var rangeCount = 0;
        foreach (var file in current.Values)
        {
            AddRange(file.Recipe);
            if (file.Index is not null) AddRange(file.Index);
            var recipe = Read<WorldFileRecipe>(accountingReader, file.Recipe, token);
            foreach (var segment in recipe.Segments)
            {
                var reference = segment.Content;
                accountingReader.ValidateDependency(file.Recipe.ArtifactId, reference.ArtifactId);
                AddRange(reference);
            }
        }
        long pinnedBytes = 0, referencedBytes = 0;
        foreach (var (id, packRanges) in ranges)
        {
            var packLength = accountingReader.GetPackLength(id);
            long end = 0;
            foreach (var range in packRanges.OrderBy(r => r.Start))
            {
                if (range.Start < 0 || range.End > packLength) throw new InvalidDataException("Reference exceeds its data pack.");
                referencedBytes = checked(referencedBytes + Math.Max(0, range.End - Math.Max(end, range.Start)));
                end = Math.Max(end, range.End);
            }
        }
        // A shared metadata pack can retain data that no current file references.
        // Count its entire declared transitive closure, not only the used ranges.
        var pendingPacks = new Stack<Guid>(ranges.Keys); var reachablePacks = new HashSet<Guid>();
        while (pendingPacks.TryPop(out var id))
        {
            token.ThrowIfCancellationRequested();
            if (!reachablePacks.Add(id)) continue;
            if (reachablePacks.Count > 4096) throw new IOException("Pack accounting node budget exceeded.");
            pinnedBytes = checked(pinnedBytes + accountingReader.GetPackLength(id));
            foreach (var dependency in accountingReader.GetDependencies(id)) pendingPacks.Push(dependency);
        }
        return new(1, sourceId, boundary, current.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray(),
            deleted.Order(StringComparer.Ordinal).ToArray(), deletedChunks, checkpoint, newBytes, updates.Count)
        { ReferencedObjectBytes = referencedBytes, PinnedPackBytes = pinnedBytes };

        void AddRange(WorldObjectReference reference)
        {
            if (!ranges.TryGetValue(reference.ArtifactId, out var packRanges)) ranges.Add(reference.ArtifactId, packRanges = []);
            if (packRanges.Add((reference.Offset, checked(reference.Offset + reference.Length))) && ++rangeCount > 1_000_000)
                throw new IOException("Reference accounting budget exceeded.");
        }

        ChunkEntry RefreshExternal(ChunkEntry chunk)
        {
            if (!chunk.External || chunk.ExternalPath is not { } path || (!updates.Contains(path) && !deleted.Contains(path))) return chunk;
            if (!allPaths.Contains(path)) return chunk with { State = SemanticState.Unavailable, PayloadHash = null, ExternalStorageHash = null, Diagnostic = "missing_external_file" };
            using var input = source.OpenRead(path);
            var rawHash = SemanticStreams.Hash(input, token); input.Position = 0;
            if (chunk.Compression is not (1 or 2 or 3)) return chunk with { ExternalStorageHash = rawHash, State = SemanticState.Unsupported, Diagnostic = "unsupported_compression" };
            try { return chunk with { State = SemanticState.Present, ExternalStorageHash = rawHash, PayloadHash = RegionCodec.HashDecoded(input, chunk.Compression, token), Diagnostic = null }; }
            catch (InvalidDataException) { return chunk with { State = SemanticState.Corrupt, ExternalStorageHash = rawHash, PayloadHash = null, Diagnostic = "invalid_compression_or_nbt" }; }
            catch (EndOfStreamException) { return chunk with { State = SemanticState.Corrupt, ExternalStorageHash = rawHash, PayloadHash = null, Diagnostic = "truncated_payload" }; }
        }
    }

    public static T Read<T>(IWorldObjectReader reader, WorldObjectReference reference, CancellationToken token)
    {
        if (reference.Length > RecipePackBytes) throw new InvalidDataException("Recipe quota exceeded.");
        using var stream = reader.Open(reference, token);
        return JsonSerializer.Deserialize<T>(stream, SemanticJson.Options) ?? throw new InvalidDataException("Empty recipe.");
    }
    private static WorldObjectReference Write<T>(IWorldObjectWriter writer, T value, IReadOnlyCollection<Guid> dependencies, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SemanticJson.Options);
        if (bytes.Length > RecipePackBytes) throw new InvalidDataException("Recipe quota exceeded.");
        using var stream = new MemoryStream(bytes, false);
        return writer.Store(stream, bytes.Length, true, dependencies, token);
    }
    public static Stream OpenFile(WorldStateFile file, IWorldObjectReader reader, CancellationToken token)
    {
        var recipe = Read<WorldFileRecipe>(reader, file.Recipe, token);
        if (recipe.Version != 1 || recipe.Length != file.Length || recipe.Encoding is not ("Raw" or "OpaqueRegion" or "SegmentedRegion"))
            throw new InvalidDataException("Invalid file recipe.");
        long end = 0;
        foreach (var segment in recipe.Segments)
        {
            reader.ValidateDependency(file.Recipe.ArtifactId, segment.Content.ArtifactId);
            if (segment.FileOffset != end || segment.Content.Length <= 0) throw new InvalidDataException("Recipe coverage is not exact.");
            end = checked(end + segment.Content.Length);
        }
        if (end != file.Length) throw new InvalidDataException("Recipe coverage differs from file length.");
        return new RecipeStream(recipe, reader, token);
    }
    internal static string HashRange(Stream input, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        while (length > 0) { token.ThrowIfCancellationRequested(); var n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length)); if (n == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, n); length -= n; }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string? ExternalOwner(string path)
    {
        var name = Path.GetFileName(path).Split('.');
        if (name.Length != 4 || name[0] != "c" || name[3] != "mcc" || !int.TryParse(name[1], out var x) || !int.TryParse(name[2], out var z)) return null;
        var parent = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        return parent + "r." + Coordinates.FloorDiv(x, 32) + "." + Coordinates.FloorDiv(z, 32) + ".mca";
    }
    private sealed class CombinedObjects(IWorldObjectReader baseline, IWorldObjectWriter current) : IWorldObjectReader
    {
        public Stream Open(WorldObjectReference reference, CancellationToken token)
            => (current.Owns(reference.ArtifactId) ? current : baseline).Open(reference, token);
        public void ValidateDependency(Guid owner, Guid dependency)
            => (current.Owns(owner) ? current : baseline).ValidateDependency(owner, dependency);
        public long GetPackLength(Guid id) => (current.Owns(id) ? current : baseline).GetPackLength(id);
        public IReadOnlyCollection<Guid> GetDependencies(Guid id) => (current.Owns(id) ? current : baseline).GetDependencies(id);
    }
    private sealed class OverlaySource(IReadOnlyWorldSource changed, IReadOnlyDictionary<string, SourceFile> inventory,
        IReadOnlyDictionary<string, WorldStateFile> baseline, IReadOnlySet<string> deleted, IWorldObjectReader reader, CancellationToken token) : IReadOnlyWorldSource
    {
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => inventory.Values
            .Concat(baseline.Values.Where(f => !inventory.ContainsKey(f.Path) && !deleted.Contains(f.Path)).Select(f => new SourceFile(f.Path, f.Length, DateTime.UnixEpoch))).ToArray();
        public Stream OpenRead(string path) => inventory.ContainsKey(path) ? changed.OpenRead(path)
            : !deleted.Contains(path) && baseline.TryGetValue(path, out var file) ? OpenFile(file, reader, token)
            : throw new FileNotFoundException("File is not in the fixed source view.", path);
    }
    private sealed class RecipeStream(WorldFileRecipe recipe, IWorldObjectReader reader, CancellationToken token) : Stream
    {
        private int _index; private Stream? _current; private long _position;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;
            while (_index < recipe.Segments.Count)
            {
                if (_current is null)
                {
                    _current = reader.Open(recipe.Segments[_index].Content, token);
                    _current.Position = _position - recipe.Segments[_index].FileOffset;
                }
                var n = _current.Read(buffer); if (n > 0) { _position += n; return n; }
                _current.Dispose(); _current = null; _index++;
            }
            return 0;
        }
        protected override void Dispose(bool disposing) { if (disposing) _current?.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => recipe.Length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > Length) throw new ArgumentOutOfRangeException(nameof(value));
                _current?.Dispose(); _current = null; _position = value; _index = 0;
                while (_index < recipe.Segments.Count && recipe.Segments[_index].FileOffset + recipe.Segments[_index].Content.Length <= value) _index++;
            }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        { Position = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length)); return Position; }
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
