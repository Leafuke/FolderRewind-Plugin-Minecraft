using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace MineRewind.World.Semantic;

public static class ScratchArchive
{
    public static ScratchCompletion Validate(ISemanticScratch scratch, CancellationToken token = default)
    {
        using var input = scratch.OpenRead("complete.json");
        if (input.Length > 65536) throw new InvalidDataException("Oversized completion record.");
        var completed = JsonSerializer.Deserialize<ScratchCompletion>(input, SemanticJson.Options) ?? throw new InvalidDataException();
        if (completed.SchemaVersion != 1 || string.IsNullOrWhiteSpace(completed.SourceId)) throw new InvalidDataException("Unsupported scratch.");
        if (completed.Summary.Files < 0 || completed.Summary.Files > SemanticPaths.MaximumInventoryEntries)
            throw new InvalidDataException("Scratch exceeds the P1 metadata budget.");
        foreach (var (path, hash) in new[] { ("files.jsonl", completed.FilesHash), ("regions.jsonl", completed.RegionsHash), ("diagnostics.jsonl", completed.DiagnosticsHash) })
            if (WorldCapture.HashFile(scratch, path, token) != hash) throw new InvalidDataException("Scratch manifest hash mismatch: " + path);
        return completed;
    }

    public static IEnumerable<T> ReadLines<T>(ISemanticScratch scratch, string relative, CancellationToken token = default)
    {
        using var stream = scratch.OpenRead(relative);
        using var reader = new StreamReader(stream);
        var buffer = new char[8192]; var pending = new StringBuilder(); int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            token.ThrowIfCancellationRequested();
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                Append(start, i - start);
                yield return JsonSerializer.Deserialize<T>(pending.ToString(), SemanticJson.Options) ?? throw new InvalidDataException();
                pending.Clear(); start = i + 1;
            }
            Append(start, read - start);
        }
        if (pending.Length != 0) throw new InvalidDataException("Manifest record is not terminated.");
        void Append(int start, int count)
        {
            if (pending.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("Oversized manifest record.");
            pending.Append(buffer, start, count);
        }
    }

    public static void Reconstruct(ISemanticScratch scratch, ISemanticScratch destination, CancellationToken token = default)
        => CopyArchive(scratch, destination, token);

    public static void VerifyIntegrity(ISemanticScratch scratch, CancellationToken token = default)
        => CopyArchive(scratch, null, token);

    private static void CopyArchive(ISemanticScratch scratch, ISemanticScratch? destination, CancellationToken token)
    {
        var completion = Validate(scratch, token);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0; string? previousPath = null;
        foreach (var file in ReadLines<StoredFile>(scratch, "files.jsonl", token))
        {
            if (!paths.Add(SemanticPaths.Normalize(file.RelativePath)) || file.Length < 0
                || previousPath is not null && StringComparer.Ordinal.Compare(previousPath, file.RelativePath) >= 0)
                throw new InvalidDataException("Invalid file manifest.");
            previousPath = file.RelativePath; totalBytes = checked(totalBytes + file.Length);
            if (file.Segments.Count > 4096) throw new InvalidDataException("Too many region segments.");
            var objectLengths = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var segment in file.Segments)
            {
                objectLengths.TryGetValue(segment.ObjectPath, out var end);
                if (segment.ObjectOffset != end || segment.Length < 0) throw new InvalidDataException("Invalid object coverage.");
                objectLengths[segment.ObjectPath] = checked(end + segment.Length);
            }
            long cursor = 0;
            using var output = destination?.Create(file.RelativePath) ?? Stream.Null;
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            var objects = new Dictionary<string, Stream>(StringComparer.Ordinal);
            try
            {
                if (objectLengths.Count > 2) throw new InvalidDataException("Too many packs for a file.");
                foreach (var name in objectLengths.Keys)
                {
                    if (!name.StartsWith("objects/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid pack location.");
                    objects.Add(name, scratch.OpenRead(name));
                }
                foreach (var segment in file.Segments)
                {
                    if (segment.FileOffset != cursor || segment.Length < 0 || segment.Length > file.Length - cursor
                        || segment.ObjectOffset < 0 || !segment.ObjectPath.StartsWith("objects/", StringComparison.Ordinal))
                        throw new InvalidDataException("Invalid segment coverage.");
                    var input = objects[segment.ObjectPath];
                    if (input.Length != objectLengths[segment.ObjectPath]) throw new InvalidDataException("Scratch object size mismatch.");
                    if (segment.ObjectOffset > input.Length || segment.Length > input.Length - segment.ObjectOffset)
                        throw new InvalidDataException("Truncated scratch object.");
                    input.Position = segment.ObjectOffset;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var remaining = segment.Length;
                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new EndOfStreamException();
                        hash.AppendData(buffer, 0, read); fileHash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read); remaining -= read;
                    }
                    if (Convert.ToHexString(hash.GetHashAndReset()) != segment.Sha256) throw new InvalidDataException("Scratch segment hash mismatch.");
                    cursor += segment.Length;
                }
                if (cursor != file.Length || Convert.ToHexString(fileHash.GetHashAndReset()) != file.Sha256)
                    throw new InvalidDataException("Reconstructed file hash/length mismatch: " + file.RelativePath);
            }
            finally { foreach (var stream in objects.Values) stream.Dispose(); }
        }
        if (paths.Count != completion.Summary.Files || totalBytes != completion.Summary.Bytes)
            throw new InvalidDataException("Completion inventory does not match files.");
        token.ThrowIfCancellationRequested();
        // The caller publishes its own result marker outside the reconstructed world.
    }

    public static VerificationResult VerifySource(IReadOnlyWorldSource source, ISemanticScratch scratch, CancellationToken token = default)
    {
        var inventory = source.GetInventory(token).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var errors = new List<string>(); int count = 0; long bytes = 0;
        foreach (var file in ReadLines<StoredFile>(scratch, "files.jsonl", token))
        {
            token.ThrowIfCancellationRequested(); count++; bytes += file.Length;
            if (!inventory.Remove(file.RelativePath, out var actual)) { errors.Add("missing:" + file.RelativePath); continue; }
            using var input = source.OpenRead(file.RelativePath);
            if (actual.Length != file.Length || SemanticStreams.Hash(input, token) != file.Sha256) errors.Add("bytes:" + file.RelativePath);
        }
        errors.AddRange(inventory.Keys.Select(p => "extra:" + p));
        return new(errors.Count == 0, count, bytes, errors);
    }
}
