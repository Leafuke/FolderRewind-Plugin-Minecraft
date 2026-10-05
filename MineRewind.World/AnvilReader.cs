using System.Buffers.Binary;
using System.IO.Compression;

namespace MineRewind.World;

public sealed record ChunkRecord(ChunkReadState State, byte[]? Nbt);

/// <summary>Read-only Anvil envelope reader. Does not normalize or write region data.</summary>
public static class AnvilReader
{
    public const int MaximumChunkBytes = 16 * 1024 * 1024;
    public static byte[] ReadIndex(string sourceRoot, string path)
    {
        using var stream = Open(sourceRoot, path);
        var header = new byte[8192]; stream.ReadExactly(header);
        return header;
    }

    public static ChunkRecord Read(string sourceRoot, string path, ChunkKey key, CancellationToken token = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = Open(sourceRoot, path);
                var before = (file.Length, File.GetLastWriteTimeUtc(path));
                var header = new byte[8192]; file.ReadExactly(header);
                var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(key.Slot * 4, 4));
                if (location == 0) return new(ChunkReadState.Absent, null);
                var offset = (long)(location >> 8) * 4096;
                var sectors = (int)(location & 255);
                if (offset < 8192 || sectors == 0 || offset + sectors * 4096L > file.Length)
                    return new(ChunkReadState.Corrupt, null);
                for (var slot = 0; slot < 1024; slot++)
                {
                    if (slot == key.Slot) continue;
                    var other = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4, 4));
                    if (other == 0) continue;
                    var start = (long)(other >> 8) * 4096;
                    if (start < offset + sectors * 4096L && start + (other & 255) * 4096L > offset)
                        return new(ChunkReadState.Corrupt, null);
                }
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
                    continue;
                return new(ChunkReadState.Present, nbt);
            }
            catch (OperationCanceledException) { throw; }
            catch (FileNotFoundException) { return new(ChunkReadState.Unavailable, null); }
            catch (UnauthorizedAccessException) { return new(ChunkReadState.Unavailable, null); }
            catch (IOException) { if (attempt == 1) return new(ChunkReadState.Corrupt, null); }
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
