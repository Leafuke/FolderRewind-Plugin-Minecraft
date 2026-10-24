using System.Buffers.Binary;
using fNbt;

namespace MineRewind.World.Semantic;

/// <summary>Metadata only. Payloads always remain in the caller's fixed input view.</summary>
public sealed record MergeChunk(int Slot, uint Timestamp, byte Compression, string? ExternalPath,
    long Offset, int Length, string PayloadHash, bool SeparatedTerrain)
{
    public bool HasEmbeddedEntities { get; init; }
}
public sealed record MergeRegionIndex(string Path, RegionAddress Address, IReadOnlyList<MergeChunk> Chunks);
public sealed record MergeChunkSource(MergeRegionIndex Region, MergeChunk Chunk, int Side);

/// <summary>Lossless Anvil composition. The caller owns all streams, inventories and output publication.</summary>
public static class MergeRegion
{
    public static bool TryAddress(string path, string[] worlds, out RegionAddress? address)
        => WorldCapture.TryAddress(path, worlds, out address);

    public static async Task<MergeRegionIndex> ReadAsync(string path, RegionAddress address,
        Func<string, CancellationToken, ValueTask<Stream>> open, IReadOnlySet<string> paths, CancellationToken token)
    {
        await using var file = await open(path, token).ConfigureAwait(false);
        if (!file.CanSeek || file.Length < 8192) throw new InvalidDataException("merge_invalid_region_header: " + path);
        var header = new byte[8192]; await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var index = new AnvilRegionIndex(header, file.Length, DateTime.UnixEpoch);
        if (index.Valid.Any(v => !v)) throw new InvalidDataException("merge_invalid_region_extents: " + path);
        var chunks = new List<MergeChunk>();
        for (var slot = 0; slot < 1024; slot++)
        {
            token.ThrowIfCancellationRequested();
            var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4, 4));
            if (location == 0) continue;
            var x = checked(address.X * 32 + slot % 32); var z = checked(address.Z * 32 + slot / 32);
            var offset = (long)(location >> 8) * 4096;
            file.Position = offset;
            var envelope = new byte[5]; await file.ReadExactlyAsync(envelope, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(envelope);
            var compression = (byte)(envelope[4] & 127); var external = (envelope[4] & 128) != 0;
            if (length < 1 || length > (location & 255) * 4096L - 4 || length > file.Length - offset - 4
                || external && length != 1 || compression is not (1 or 2 or 3))
                throw new InvalidDataException("merge_unsupported_chunk_envelope: " + path);
            var externalPath = external ? path[..(path.LastIndexOf('/') + 1)] + $"c.{x}.{z}.mcc" : null;
            if (externalPath is not null && !paths.Contains(externalPath)) throw new InvalidDataException("merge_missing_external: " + externalPath);
            await using var payload = externalPath is not null ? await open(externalPath, token).ConfigureAwait(false)
                : new RegionCodec.BoundedReadStream(file, length - 1);
            int? dataVersion = null, chunkX = null, chunkZ = null;
            var embedded = false; var unknownEntities = false; var legacy = false;
            var hash = RegionCodec.HashDecoded(payload, compression, token, reader =>
            {
                if (reader.TagName is "Entities" or "entities")
                {
                    // Root entity lists in modern proto-chunks are distinct from unknown layouts.
                    // Empty lists contain nothing to mix; populated lists require preserving Ours.
                    var rootList = reader.Depth == 2 && reader.TagType == NbtTagType.List
                        && reader.ListType is NbtTagType.End or NbtTagType.Compound;
                    unknownEntities |= !rootList;
                    embedded |= rootList && reader.TagLength > 0;
                }
                if (reader.Depth == 2 && reader.TagName == "Level") legacy = true;
                if (reader.Depth == 2 && reader.TagType == NbtTagType.Int)
                {
                    switch (reader.TagName)
                    {
                        case "DataVersion": dataVersion = reader.ReadValueAs<int>(); break;
                        case "xPos": chunkX = reader.ReadValueAs<int>(); break;
                        case "zPos": chunkZ = reader.ReadValueAs<int>(); break;
                    }
                }
            });
            // Root-level position tags identify the modern terrain layout; legacy/mixed layouts
            // remain valid opaque payloads, but cannot participate in terrain-first composition.
            var knownModern = dataVersion > 0 && chunkX == x && chunkZ == z && !legacy && !unknownEntities;
            var separated = knownModern && !embedded;
            if (address.Component == ChunkComponentKind.Terrain && (chunkX.HasValue && chunkX != x || chunkZ.HasValue && chunkZ != z))
                throw new InvalidDataException("merge_chunk_coordinate_mismatch: " + path);
            chunks.Add(new(slot, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4096 + slot * 4, 4)),
                compression, externalPath, offset + 5, length - 1, hash, separated) { HasEmbeddedEntities = knownModern && embedded });
        }
        return new(path, address, chunks);
    }

    public static async Task WriteAsync(IReadOnlyList<MergeChunkSource> selected, Stream output,
        Func<int, string, CancellationToken, ValueTask<Stream>> open, CancellationToken token)
    {
        if (selected.Select(s => s.Chunk.Slot).Distinct().Count() != selected.Count) throw new InvalidDataException("Duplicate output slot.");
        var header = new byte[8192]; var sector = 2;
        foreach (var item in selected.OrderBy(s => s.Chunk.Slot))
        {
            var chunk = item.Chunk;
            if (chunk.Slot is < 0 or >= 1024) throw new InvalidDataException("Invalid output slot.");
            var count = chunk.ExternalPath is not null ? 1 : checked((chunk.Length + 5 + 4095) / 4096);
            if (count is < 1 or > 255 || sector > 0xFFFFFF) throw new InvalidDataException("Unrepresentable output region.");
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(chunk.Slot * 4, 4), (uint)(sector << 8 | count));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4096 + chunk.Slot * 4, 4), chunk.Timestamp);
            sector += count;
        }
        await output.WriteAsync(header, token).ConfigureAwait(false);
        var buffer = new byte[65536]; var padding = new byte[4096];
        var inputs = new Dictionary<int, Stream>();
        try
        {
        if (selected.Select(s => s.Region.Path).Distinct(StringComparer.Ordinal).Count() > 1) throw new InvalidDataException("Output combines different region coordinates.");
        foreach (var item in selected.Where(s => s.Chunk.ExternalPath is null).DistinctBy(s => s.Side))
            inputs.Add(item.Side, await open(item.Side, item.Region.Path, token).ConfigureAwait(false));
        foreach (var item in selected.OrderBy(s => s.Chunk.Slot))
        {
            var chunk = item.Chunk;
            var envelope = new byte[5];
            BinaryPrimitives.WriteInt32BigEndian(envelope, chunk.ExternalPath is not null ? 1 : chunk.Length + 1);
            envelope[4] = (byte)(chunk.Compression | (chunk.ExternalPath is not null ? 128 : 0));
            await output.WriteAsync(envelope, token).ConfigureAwait(false);
            if (chunk.ExternalPath is null)
            {
                var input = inputs[item.Side];
                input.Position = chunk.Offset;
                var remaining = chunk.Length;
                while (remaining > 0)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); remaining -= count;
                }
            }
            var bytes = chunk.ExternalPath is not null ? 5 : chunk.Length + 5;
            var pad = (4096 - bytes % 4096) % 4096;
            await output.WriteAsync(padding.AsMemory(0, pad), token).ConfigureAwait(false);
        }
        }
        finally { foreach (var input in inputs.Values) await input.DisposeAsync().ConfigureAwait(false); }
    }
}
