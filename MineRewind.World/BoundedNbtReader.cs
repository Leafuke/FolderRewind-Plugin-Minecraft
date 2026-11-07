using System.IO.Compression;
using fNbt;

namespace MineRewind.World;

internal sealed record NbtReadLimits(int InputBytes, int DecodedBytes)
{
    internal static NbtReadLimits Display { get; } = new(16 * 1024 * 1024, 16 * 1024 * 1024);
    internal static NbtReadLimits Preservation { get; } = new(64 * 1024 * 1024, 64 * 1024 * 1024);
}

/// <summary>Validates before allocating tag arrays. Neither compression nor object parsing can bypass cancellation.</summary>
internal static class BoundedNbtReader
{
    internal static NbtFile Read(Stream source, NbtReadLimits limits, CancellationToken token, TagSelector? selector = null)
    {
        var encoded = ReadBytes(source, limits.InputBytes, token);
        using var input = new MemoryStream(encoded, writable: false);
        using Stream decoded = encoded.Length >= 2 && encoded[0] == 0x1f && encoded[1] == 0x8b
            ? new GZipStream(input, CompressionMode.Decompress)
            : encoded.Length > 0 && encoded[0] == 0x78 ? new ZLibStream(input, CompressionMode.Decompress) : input;
        var bytes = ReadBytes(decoded, limits.DecodedBytes, token);
        using var checkedInput = new CancellationStream(bytes, token);
        var reader = new NbtReader(checkedInput);
        while (reader.ReadToFollowing())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 64 || reader.TagsRead > 100_000
                || reader.HasLength && (reader.TagLength < 0 || reader.TagLength > 1_048_576))
                throw new InvalidDataException("NBT structure exceeds its read budget.");
        }
        checkedInput.Position = 0;
        var result = new NbtFile();
        result.LoadFromStream(checkedInput, NbtCompression.None,
            tag => { token.ThrowIfCancellationRequested(); return selector?.Invoke(tag) ?? true; });
        token.ThrowIfCancellationRequested();
        return result;
    }
    private static byte[] ReadBytes(Stream source, int maximum, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            count = source.Read(buffer, 0, Math.Min(buffer.Length, maximum - checked((int)result.Length) + 1));
            if (count == 0) return result.ToArray();
            if (result.Length + count > maximum) throw new InvalidDataException("NBT byte budget exceeded.");
            result.Write(buffer, 0, count);
        }
    }
    private sealed class CancellationStream(byte[] bytes, CancellationToken token) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return base.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return base.Read(buffer); }
        public override int ReadByte() { token.ThrowIfCancellationRequested(); return base.ReadByte(); }
        public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return base.Seek(offset, origin); }
    }
}
