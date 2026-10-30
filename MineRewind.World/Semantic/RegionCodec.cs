using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using fNbt;

namespace MineRewind.World.Semantic;

internal sealed record PayloadSlice(long Offset, long Length, string Sha256);
internal sealed record RegionAnalysis(RegionEntry Entry, IReadOnlyList<PayloadSlice> Payloads);

internal static class RegionCodec
{
    public const int MaximumDecodedBytes = 16 * 1024 * 1024;
    // At most one fixed decoding buffer, plus two copy buffers and bounded NBT reader state.
    public const long BufferBudget = MaximumDecodedBytes + 4L * SemanticStreams.BufferSize + 8192;
    private static readonly byte[] HashPrefix = Encoding.UTF8.GetBytes("minerewind/opaque-nbt-bytes/1\0");

    public static RegionAnalysis Analyze(IReadOnlyWorldSource source, SourceFile info, Stream file,
        RegionAddress address, IReadOnlySet<string> paths, CancellationToken token, RegionEntry? baseline = null)
    {
        var chunks = new List<ChunkEntry>(); var payloads = new List<PayloadSlice>(); var diagnostics = new List<string>();
        var state = SemanticState.Present;
        if (file.Length < 8192) return Failed(file.Length == 0 ? "empty_region_without_header" : "truncated_header", SemanticState.Corrupt);
        file.Position = 0;
        var header = new byte[8192]; file.ReadExactly(header);
        var index = new AnvilRegionIndex(header, file.Length, info.LastWriteUtc);
        if (index.Valid.Any(v => !v)) return Failed("invalid_or_overlapping_extents", SemanticState.Corrupt);
        for (var slot = 0; slot < 1024; slot++)
        {
            token.ThrowIfCancellationRequested();
            var x = checked(address.X * 32 + slot % 32); var z = checked(address.Z * 32 + slot / 32);
            var timestamp = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4096 + slot * 4, 4));
            var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4, 4));
            if (location == 0) { chunks.Add(new(slot, x, z, SemanticState.Absent, timestamp, 0, false, null, null, null)); continue; }
            var offset = (long)(location >> 8) * 4096; var sectors = location & 255;
            file.Position = offset;
            var envelope = new byte[5]; file.ReadExactly(envelope);
            var length = BinaryPrimitives.ReadInt32BigEndian(envelope);
            var compression = (byte)(envelope[4] & 127); var external = (envelope[4] & 128) != 0;
            var externalPath = external ? JoinParent(info.RelativePath, $"c.{x}.{z}.mcc") : null;
            string? hash = null, diagnostic = null, externalHash = null, storageHash = null; var chunkState = SemanticState.Present;
            var previous = baseline?.Chunks.FirstOrDefault(c => c.Slot == slot);
            if (length < 1 || length > sectors * 4096L - 4 || length > file.Length - offset - 4 || (external && length != 1))
                (chunkState, diagnostic) = (SemanticState.Corrupt, "invalid_record_length");
            else if (compression is not (1 or 2 or 3))
                (chunkState, diagnostic) = (SemanticState.Unsupported, "unsupported_compression");
            else if (external && !paths.Contains(externalPath!))
                (chunkState, diagnostic) = (SemanticState.Unavailable, "missing_external_file");
            else
            {
                try
                {
                    if (external)
                    {
                        using var input = source.OpenRead(externalPath!);
                        externalHash = SemanticStreams.Hash(input, token); input.Position = 0;
                        hash = previous is { State: SemanticState.Present, External: true }
                            && previous.Compression == compression && previous.ExternalStorageHash == externalHash
                            ? previous.PayloadHash : HashDecoded(input, compression, token);
                    }
                    else
                    {
                        using (var raw = new BoundedReadStream(file, length - 1)) storageHash = SemanticStreams.Hash(raw, token);
                        file.Position = offset + 5;
                        using var input = new BoundedReadStream(file, length - 1);
                        hash = previous is { State: SemanticState.Present, External: false }
                            && previous.Compression == compression && previous.PayloadStorageHash == storageHash
                            ? previous.PayloadHash : HashDecoded(input, compression, token);
                        payloads.Add(new(offset + 5, length - 1, storageHash!));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (DecodeLimitException) { (chunkState, diagnostic) = (SemanticState.Unsupported, "decode_limit"); }
                catch (InvalidDataException) { (chunkState, diagnostic) = (SemanticState.Corrupt, "invalid_compression_or_nbt"); }
                catch (EndOfStreamException) { (chunkState, diagnostic) = (SemanticState.Corrupt, "truncated_payload"); }
                catch (IOException) { (chunkState, diagnostic) = (SemanticState.Unavailable, "payload_unavailable"); }
                catch (UnauthorizedAccessException) { (chunkState, diagnostic) = (SemanticState.Unavailable, "payload_unavailable"); }
            }
            chunks.Add(new(slot, x, z, chunkState, timestamp, compression, external, externalPath, hash, diagnostic)
            { ExternalStorageHash = externalHash, PayloadStorageHash = storageHash });
            if (chunkState != SemanticState.Present)
            {
                state = chunkState;
                diagnostics.Add($"slot:{slot}:{diagnostic}");
            }
        }
        return new(new(info.RelativePath, address, "", state, chunks, diagnostics), payloads);

        RegionAnalysis Failed(string code, SemanticState failure) => new(
            new(info.RelativePath, address, "", failure, [], [code]), []);
    }

    internal static string HashDecoded(Stream input, byte compression, CancellationToken token, Action<NbtReader>? inspect = null,
        Action<byte[], int>? inspectPayload = null)
    {
        using Stream decoded = compression switch
        {
            1 => new GZipStream(input, CompressionMode.Decompress, leaveOpen: true),
            2 => new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true),
            _ => new BoundedReadStream(input, long.MaxValue)
        };
        // Fixed cap avoids MemoryStream growth and a second full-payload copy.
        var bytes = ArrayPool<byte>.Shared.Rent(MaximumDecodedBytes);
        var count = 0;
        try
        {
            while (count < MaximumDecodedBytes)
            {
                token.ThrowIfCancellationRequested();
                var read = decoded.Read(bytes, count, Math.Min(65536, MaximumDecodedBytes - count));
                if (read == 0) break;
                count += read;
            }
            if (count == MaximumDecodedBytes && decoded.ReadByte() != -1) throw new DecodeLimitException();
            using var nbt = new MemoryStream(bytes, 0, count, writable: false);
            try
            {
                var reader = new NbtReader(nbt);
                var seenRoot = false;
                while (reader.ReadToFollowing())
                {
                    token.ThrowIfCancellationRequested();
                    if (!seenRoot && (reader.TagType != NbtTagType.Compound || reader.Depth != 1))
                        throw new InvalidDataException("Expected compound root.");
                    seenRoot = true;
                    if (reader.Depth > 64 || reader.TagsRead > 100_000
                        || (reader.HasLength && (reader.TagLength < 0 || reader.TagLength > 1_048_576)))
                        throw new DecodeLimitException();
                    inspect?.Invoke(reader);
                }
                if (!seenRoot || nbt.Position != count) throw new InvalidDataException("Invalid NBT framing.");
            }
            catch (NbtFormatException ex) { throw new InvalidDataException("Malformed NBT.", ex); }
            inspectPayload?.Invoke(bytes, count);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(HashPrefix); hash.AppendData(bytes, 0, count);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { Array.Clear(bytes, 0, count); ArrayPool<byte>.Shared.Return(bytes); }
    }

    private static string JoinParent(string relative, string name)
        => relative[..(relative.LastIndexOf('/') + 1)] + name;
    private sealed class DecodeLimitException : IOException { }

    internal sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]); remaining -= read; return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
