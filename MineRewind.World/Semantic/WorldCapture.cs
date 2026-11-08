using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MineRewind.World.Semantic;

public static class WorldCapture
{
    public static ScratchCompletion Capture(IReadOnlyWorldSource source, ISemanticScratch scratch,
        string sourceId, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (scratch.Exists("complete.json")) throw new IOException("Scratch already completed.");
        var timer = Stopwatch.StartNew();
        var sourceInventory = source.GetInventory(token);
        if (sourceInventory.Count > SemanticPaths.MaximumInventoryEntries
            || sourceInventory.Sum(f => (long)f.RelativePath.Length) > SemanticPaths.MaximumInventoryPathCharacters)
            throw new IOException("Offline inventory exceeds the P1 metadata budget.");
        var inventory = sourceInventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
        var paths = inventory.Select(f => SemanticPaths.Normalize(f.RelativePath)).ToHashSet(StringComparer.Ordinal);
        if (paths.Count != inventory.Length || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            throw new InvalidDataException("Duplicate source paths.");
        var regionAddresses = WorldLayoutReader.FromInventory(inventory.Select(f => f.RelativePath))
            .SelectMany(world => world.Dimensions.SelectMany(dimension => dimension.RegionFiles.Select(file =>
                (file.RelativePath, Address: new RegionAddress(world.WorldId, dimension.Id, file.Region.X, file.Region.Z, file.Component)))))
            .ToDictionary(item => item.RelativePath, item => item.Address, StringComparer.Ordinal);
        var regions = new List<(RegionAddress Address, string Manifest)>();
        var externalHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var referencedExternal = new HashSet<string>(StringComparer.Ordinal);
        var rawHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        int present = 0, comparable = 0, opaque = 0, missing = 0, unclassified = 0;
        long bytes = 0;
        using (var filesWriter = Writer(scratch.Create("files.jsonl")))
        using (var diagnosticsWriter = Writer(scratch.Create("diagnostics.jsonl")))
        {
            for (var number = 0; number < inventory.Length; number++)
            {
                token.ThrowIfCancellationRequested();
                var info = inventory[number];
                using var input = source.OpenRead(info.RelativePath);
                if (!input.CanSeek || input.Length != info.Length) throw new IOException("Source changed or is not seekable: " + info.RelativePath);
                var originalHash = SemanticStreams.Hash(input, token);
                input.Position = 0;
                if (info.Sha256 is { } expected && !StringComparer.OrdinalIgnoreCase.Equals(originalHash, expected))
                    throw new IOException("Captured input hash differs from the Host inventory: " + info.RelativePath);
                long copiedBytes = 0;
                RegionAnalysis? analysis = null;
                if (regionAddresses.TryGetValue(info.RelativePath, out var address))
                    analysis = RegionCodec.Analyze(source, info, input, address, paths, token);
                else if (info.RelativePath.EndsWith(".mca", StringComparison.OrdinalIgnoreCase))
                {
                    unclassified++;
                    Write(diagnosticsWriter, new ProbeDiagnostic(info.RelativePath, "unclassified_region_preserved"));
                }
                var good = analysis?.Entry.State == SemanticState.Present;
                var encoding = analysis is null ? FileEncoding.Raw : good ? FileEncoding.SegmentedRegion : FileEncoding.OpaqueRegion;
                var prefix = $"objects/{number:D8}";
                var segments = new List<StoredSegment>();
                if (good)
                {
                    using var payload = scratch.Create(prefix + "/payload.bin");
                    using var residual = scratch.Create(prefix + "/residual.bin");
                    long cursor = 0;
                    foreach (var slice in analysis!.Payloads.OrderBy(s => s.Offset))
                    {
                        if (slice.Offset < cursor || slice.Length < 0 || slice.Offset + slice.Length > input.Length)
                            throw new InvalidDataException("Overlapping payload slices.");
                        Store(cursor, slice.Offset - cursor, residual, prefix + "/residual.bin");
                        Store(slice.Offset, slice.Length, payload, prefix + "/payload.bin", slice.Sha256);
                        cursor = slice.Offset + slice.Length;
                    }
                    Store(cursor, input.Length - cursor, residual, prefix + "/residual.bin");
                }
                else
                {
                    using var raw = scratch.Create(prefix + "/raw.bin");
                    Store(0, input.Length, raw, prefix + "/raw.bin");
                }
                input.Position = 0;
                if (originalHash != SemanticStreams.Hash(input, token)) throw new IOException("Changing input: " + info.RelativePath);
                Write(filesWriter, new StoredFile(info.RelativePath, input.Length, originalHash, encoding, segments));
                rawHashes.Add(info.RelativePath, originalHash); bytes = checked(bytes + input.Length);
                if (analysis is not null)
                {
                    var entry = analysis.Entry with { FileHash = originalHash };
                    var manifest = prefix + "/region.json";
                    using (var output = scratch.Create(manifest)) JsonSerializer.Serialize(output, entry, SemanticJson.Options);
                    regions.Add((entry.Address, manifest));
                    if (!good) opaque++;
                    foreach (var chunk in entry.Chunks)
                    {
                        if (chunk.State != SemanticState.Absent) present++;
                        if (chunk.State == SemanticState.Present && good) comparable++;
                        if (chunk.ExternalPath is { } external)
                        {
                            // Missing references are counted, not cached: malformed worlds can name
                            // 1024 absent mcc files per region, far beyond the bounded file inventory.
                            if (paths.Contains(external)) referencedExternal.Add(external);
                            else missing++;
                            if (chunk.ExternalStorageHash is { } hash
                                && externalHashes.TryGetValue(external, out var previous) && previous != hash)
                                throw new IOException("External chunk changed between readers: " + external);
                            if (chunk.ExternalStorageHash is { } digest) externalHashes[external] = digest;
                        }
                    }
                    foreach (var diagnostic in entry.Diagnostics)
                        Write(diagnosticsWriter, new ProbeDiagnostic(info.RelativePath, diagnostic));
                }

                void Store(long offset, long length, Stream output, string name, string? analyzedHash = null)
                {
                    if (length == 0 && info.Length != 0) return;
                    if (offset != copiedBytes) throw new InvalidDataException("Capture segments do not exactly cover the input.");
                    input.Position = offset; var outputOffset = output.Position;
                    var hash = SemanticStreams.Copy(input, output, length, token);
                    if (analyzedHash is not null && analyzedHash != hash) throw new IOException("Analyzed region payload changed before capture.");
                    copiedBytes = checked(copiedBytes + length);
                    segments.Add(new(offset, length, name, outputOffset, hash));
                }
            }
            foreach (var file in inventory.Where(f => f.RelativePath.EndsWith(".mcc", StringComparison.OrdinalIgnoreCase)
                && !referencedExternal.Contains(f.RelativePath)))
                Write(diagnosticsWriter, new ProbeDiagnostic(file.RelativePath, "orphan_external_preserved"));
        }
        foreach (var pair in externalHashes)
            if (!rawHashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
                throw new IOException("External payload changed during capture: " + pair.Key);
        var sortedRegions = regions.OrderBy(r => r.Address.SortKey, StringComparer.Ordinal).ToArray();
        if (sortedRegions.Select(r => r.Address.SortKey).Distinct(StringComparer.Ordinal).Count() != sortedRegions.Length)
            throw new InvalidDataException("Ambiguous dimension/region identity.");
        using (var writer = Writer(scratch.Create("regions.jsonl")))
            foreach (var region in sortedRegions)
            {
                using var input = scratch.OpenRead(region.Manifest);
                var entry = JsonSerializer.Deserialize<RegionEntry>(input, SemanticJson.Options) ?? throw new InvalidDataException();
                Write(writer, entry);
            }

        // Recheck bytes and inventory after all dependencies have been read. This is not a game snapshot.
        var verification = ScratchArchive.VerifySource(source, scratch, token);
        if (!verification.Success || !inventory.SequenceEqual(source.GetInventory(token).OrderBy(f => f.RelativePath, StringComparer.Ordinal)))
            throw new IOException("Changing source view: " + string.Join(", ", verification.Errors.Take(8)));
        var orphan = inventory.Count(f => f.RelativePath.EndsWith(".mcc", StringComparison.OrdinalIgnoreCase) && !referencedExternal.Contains(f.RelativePath));
        var summary = new CaptureSummary(inventory.Length, regions.Count, bytes, present, comparable, opaque,
            missing, orphan, unclassified, RegionCodec.BufferBudget, timer.Elapsed.TotalSeconds);
        var completion = new ScratchCompletion(1, sourceId, HashFile(scratch, "files.jsonl", token),
            HashFile(scratch, "regions.jsonl", token), HashFile(scratch, "diagnostics.jsonl", token), summary);
        token.ThrowIfCancellationRequested();
        using (var output = scratch.Create("complete.json")) JsonSerializer.Serialize(output, completion, SemanticJson.Options);
        return completion;
    }

    internal static StreamWriter Writer(Stream output) => new(output, new UTF8Encoding(false), 65536);
    internal static void Write<T>(StreamWriter writer, T value) => writer.WriteLine(JsonSerializer.Serialize(value, SemanticJson.Options));
    internal static string HashFile(ISemanticScratch scratch, string path, CancellationToken token)
    { using var stream = scratch.OpenRead(path); return SemanticStreams.Hash(stream, token); }

    internal static bool TryAddress(string path, string[] worlds, out RegionAddress? address)
        => MinecraftPathGrammar.TryRegionAddress(path, worlds, out address);
}
