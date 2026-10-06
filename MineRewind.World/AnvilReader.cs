using System.Buffers.Binary;
using System.IO.Compression;

namespace MineRewind.World;

public sealed record ChunkRecord(ChunkReadState State, byte[]? Nbt);

/// <summary>Validated header extents, reusable only while the file stamp still agrees.</summary>
public sealed class AnvilRegionIndex(byte[] header, long length, DateTime writeTime)
{
    public byte[] Header { get; } = header;
    public long Length { get; } = length;
    public DateTime WriteTime { get; } = writeTime;
    public bool[] Valid { get; } = Validate(header, length);
    private static bool[] Validate(byte[] header, long length)
    {
        var valid = new bool[1024];
        var extents = new List<(long Start, long End, int Slot)>();
        for (var slot = 0; slot < 1024; slot++)
        {
            var entry = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4, 4));
            if (entry == 0) { valid[slot] = true; continue; }
            var start = (long)(entry >> 8) * 4096; var end = start + (entry & 255) * 4096L;
            valid[slot] = start >= 8192 && end > start && end <= length;
            extents.Add((start, end, slot));
        }
        var ordered = extents.OrderBy(e => e.Start).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            for (var j = i + 1; j < ordered.Length && ordered[j].Start < ordered[i].End; j++)
                valid[ordered[i].Slot] = valid[ordered[j].Slot] = false;
        return valid;
    }
}

/// <summary>Read-only Anvil envelope reader. Does not normalize or write region data.</summary>
public static class AnvilReader
{
    public const int MaximumChunkBytes = 16 * 1024 * 1024;
    public static byte[] ReadIndex(string sourceRoot, string path)
        => ReadRegionIndex(sourceRoot, path).Header;
    public static AnvilRegionIndex ReadRegionIndex(string sourceRoot, string path)
    {
        using var stream = Open(sourceRoot, path);
        var header = new byte[8192]; stream.ReadExactly(header);
        return new(header, stream.Length, File.GetLastWriteTimeUtc(path));
    }

    public static ChunkRecord Read(string sourceRoot, string path, ChunkKey key, CancellationToken token = default, AnvilRegionIndex? cachedIndex = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = Open(sourceRoot, path);
                var before = (file.Length, File.GetLastWriteTimeUtc(path));
                var index = cachedIndex;
                if (index is null || index.Length != before.Length || index.WriteTime != before.Item2)
                {
                    var fresh = new byte[8192]; file.ReadExactly(fresh);
                    index = new(fresh, before.Length, before.Item2);
                }
                var header = index.Header;
                var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(key.Slot * 4, 4));
                if (location == 0) return new(ChunkReadState.Absent, null);
                var offset = (long)(location >> 8) * 4096;
                var sectors = (int)(location & 255);
                if (!index.Valid[key.Slot])
                    return new(ChunkReadState.Corrupt, null);
                file.Position = offset;
                Span<byte> envelope = stackalloc byte[5]; file.ReadExactly(envelope);
                var length = BinaryPrimitives.ReadInt32BigEndian(envelope);
                if (length < 1 || length > sectors * 4096 - 4) return new(ChunkReadState.Corrupt, null);
                var compression = envelope[4] & 127;
                if (compression is not (1 or 2 or 3)) return new(ChunkReadState.UnsupportedCompression, null);
                byte[] compressed;
                (long Length, DateTime Time)? externalStamp = null;
                string? externalPath = null;
                if ((envelope[4] & 128) != 0)
                {
                    if (length != 1) return new(ChunkReadState.Corrupt, null);
                    externalPath = Path.Combine(Path.GetDirectoryName(path)!, $"c.{key.X}.{key.Z}.mcc");
                    using var external = Open(sourceRoot, externalPath);
                    externalStamp = (external.Length, File.GetLastWriteTimeUtc(externalPath));
                    compressed = ReadBounded(external, token);
                }
                else
                {
                    compressed = new byte[length - 1]; file.ReadExactly(compressed);
                }
                using var input = new MemoryStream(compressed, writable: false);
                using Stream decoded = compression switch
                {
                    1 => new GZipStream(input, CompressionMode.Decompress),
                    2 => new ZLibStream(input, CompressionMode.Decompress),
                    _ => input
                };
                var nbt = ReadBounded(decoded, token);
                file.Position = 0;
                var afterHeader = new byte[8192]; file.ReadExactly(afterHeader);
                if (before != (file.Length, File.GetLastWriteTimeUtc(path)) || !header.AsSpan().SequenceEqual(afterHeader)
                    || (externalStamp is { } stamp && stamp != (new FileInfo(externalPath!).Length, File.GetLastWriteTimeUtc(externalPath!))))
                { cachedIndex = null; continue; }
                return new(ChunkReadState.Present, nbt);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { return new(ChunkReadState.Corrupt, null); }
            catch (EndOfStreamException) { return new(ChunkReadState.Corrupt, null); }
            catch (FileNotFoundException) { return new(ChunkReadState.Unavailable, null); }
            catch (UnauthorizedAccessException) { return new(ChunkReadState.Unavailable, null); }
            catch (IOException) { return new(ChunkReadState.Unavailable, null); }
        }
        return new(ChunkReadState.Changing, null);
    }

    private static FileStream Open(string sourceRoot, string path) => new(
        WorldPaths.RequireContained(sourceRoot, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadBounded(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + count > MaximumChunkBytes) throw new InvalidDataException("Chunk exceeds the decoding limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
