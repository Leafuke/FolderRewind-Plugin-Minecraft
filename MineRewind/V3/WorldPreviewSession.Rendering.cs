using System.Buffers.Binary;
using System.Diagnostics;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

internal sealed partial class WorldPreviewSession
{
    private sealed record TileKey(Guid Generation, string Layer, int Height, int X, int Z, int Units, SurfaceViewMode Mode, bool Overlay);
    private sealed record HeaderKey(string Path);
    private sealed class Sample(ChunkSurface surface, int attempts, long retryAt)
    {
        public SurfaceThumbnail Thumbnail { get; } = new(surface);
        public int Attempts { get; } = attempts;
        public long RetryAt { get; } = retryAt;
        public bool Retry => Thumbnail.State is ChunkReadState.Unavailable or ChunkReadState.Changing && Attempts < 3;
    }
    private sealed class TileWork(TileKey key, RegionKey[] regions)
    {
        public readonly object Gate = new();
        public byte[] Pixels { get; } = new byte[256 * 256 * 4];
        public RegionKey[] Regions { get; } = regions;
        public int RegionCursor;
        public int RegionSlot;
        public int Cursor;
        public int[] CoarseCounts { get; } = new int[7];
        public int Remaining = key.Units < 16 ? 256 * key.Units / 16 * (256 * key.Units / 16) : 0;
        public byte[] States { get; } = new byte[key.Units < 16 ? 256 * key.Units / 16 * (256 * key.Units / 16) : 0];
        public readonly HashSet<ChunkReadState> HeaderErrors = [];
        // Coarse accumulation includes coverage (alpha); empty chunks do not become opaque terrain.
        public uint[][]? Sums { get; } = key.Units >= 16 ? [new uint[65536], new uint[65536], new uint[65536], new uint[65536]] : null;
        public long Bytes => 512L + Pixels.Length + States.Length + Regions.Length * 8L + (Sums is null ? 0 : 4L * 65536 * 4);
    }

    private SpatialPreviewTile RenderProgressive(SpatialPreviewTileRequest request, CancellationToken token)
    {
        if (!double.IsFinite(request.OriginX) || !double.IsFinite(request.OriginY)
            || Math.Abs(request.OriginX) > 31_100_000 || Math.Abs(request.OriginY) > 31_100_000
            || request.UnitsPerPixel is < 1 or > 4096 || !IsPowerOfTwo(request.UnitsPerPixel)
            || request.OriginX % 16 != 0 || request.OriginY % 16 != 0)
            throw new ArgumentException("Invalid world preview tile coordinates.");
        var world = GetWorld(request.Source, token);
        var dimension = world.Dimensions.Single(d => d.Id == request.LayerId);
        var key = new TileKey(request.Source.Generation, dimension.Id, Math.Clamp(request.MaximumHeight ?? 2047, -2048, 2047),
            (int)request.OriginX, (int)request.OriginY, (int)request.UnitsPerPixel, ParseMode(request.ViewModeId), request.ShowStatusOverlay);
        TileWork work;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            work = _cache.Get<TileWork>(key)!;
            if (work is null)
            {
                var span = 256d * key.Units;
                var regions = dimension.Regions.Keys.Where(r => r.X * 512d < key.X + span && r.Z * 512d < key.Z + span
                    && (r.X + 1d) * 512 > key.X && (r.Z + 1d) * 512 > key.Z).ToArray();
                work = new(key, regions);
                _cache.Put(key, work, work.Bytes, 2);
            }
        }
        // Tile work itself is shared: overlapping requests advance the same cursor, never duplicate raster state.
        while (!Monitor.TryEnter(work.Gate, 25)) token.ThrowIfCancellationRequested();
        try
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var headerCount = 0;
            while (work.RegionCursor < work.Regions.Length && headerCount < 64 && watch.ElapsedMilliseconds < 50)
            {
                token.ThrowIfCancellationRequested();
                var region = work.Regions[work.RegionCursor];
                DrawRegion(world, dimension, key, work, region, token);
                work.RegionCursor++; work.RegionSlot = 0; headerCount++;
            }
            if (key.Units < 16 && work.RegionCursor == work.Regions.Length)
            {
                work.HeaderErrors.Clear();
                var width = 256 * key.Units / 16;
                var visited = 0; var decoded = 0;
                while (visited < work.States.Length && decoded < 64 && watch.ElapsedMilliseconds < 50)
                {
                    token.ThrowIfCancellationRequested();
                    var index = work.Cursor; work.Cursor = (work.Cursor + 1) % work.States.Length; visited++;
                    if ((work.States[index] & 128) != 0) continue;
                    var chunk = new ChunkKey(key.X / 16 + index % width, key.Z / 16 + index / width);
                    bool absent;
                    lock (_sync)
                    {
                        absent = !dimension.Regions.TryGetValue(chunk.Region, out var regionPath)
                            || _cache.Get<AnvilRegionIndex>(new HeaderKey(regionPath)) is { } regionIndex
                                && BinaryPrimitives.ReadUInt32BigEndian(regionIndex.Header.AsSpan(chunk.Slot * 4, 4)) == 0;
                    }
                    if (absent)
                    {
                        FillChunk(work.Pixels, key, chunk, (x, z) => key.Overlay ? NoDataColor(x, z) : 0);
                        work.States[index] = 128 + (byte)ChunkReadState.Absent + 1; work.Remaining--; continue;
                    }
                    var ck = new CacheKey(key.Generation, key.Layer, chunk, key.Height, key.Mode);
                    Sample? sample;
                    lock (_sync) sample = _cache.Get<Sample>(ck);
                    if (sample is null || sample.Retry && Environment.TickCount64 >= sample.RetryAt)
                    {
                        var surface = Surface(world, dimension, key.Generation, chunk, key.Height, token, key.Mode);
                        decoded++;
                        var attempts = (sample?.Attempts ?? 0) + 1;
                        sample = new(surface, attempts, Environment.TickCount64 + (attempts == 1 ? 500 : 1500));
                        lock (_sync)
                        {
                            ObjectDisposedException.ThrowIf(_closed, this);
                            _cache.Put(ck, sample, sample.Thumbnail.EstimatedBytes + 128);
                        }
                    }
                    lock (_sync)
                    {
                        ShadeAndPaint(key, work, chunk, sample);
                        // A new sample changes the shadow at adjacent chunk boundaries already in this raster.
                        foreach (var neighbor in Neighbors(chunk))
                            if (_cache.Get<Sample>(ck with { Chunk = neighbor }) is { } other) ShadeAndPaint(key, work, neighbor, other);
                    }
                    work.States[index] = Encode(sample.Thumbnail);
                    if (!sample.Retry) { work.States[index] |= 128; work.Remaining--; }
                }
            }
            var final = work.RegionCursor == work.Regions.Length && work.Remaining == 0;
            if (final && key.Units < 16)
            {
                // Requeries after neighboring tiles finish only repaint cached boundary samples, without decoding.
                var width = 256 * key.Units / 16;
                lock (_sync)
                    for (var i = 0; i < width; i++)
                        foreach (var chunk in new[] { new ChunkKey(key.X / 16 + i, key.Z / 16), new(key.X / 16 + i, key.Z / 16 + width - 1),
                            new(key.X / 16, key.Z / 16 + i), new(key.X / 16 + width - 1, key.Z / 16 + i) })
                            if (_cache.Get<Sample>(new CacheKey(key.Generation, key.Layer, chunk, key.Height, key.Mode)) is { } sample) ShadeAndPaint(key, work, chunk, sample);
            }
            var states = work.States.Where(s => s != 0).Select(s => (ChunkReadState)((s & 15) - 1))
                .Concat(work.HeaderErrors).Distinct().Where(s => s is not (ChunkReadState.Present or ChunkReadState.Absent));
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                // The raster survives sample eviction, so a large tile converges within a bounded cache.
                if (final && key.Units >= 16) _cache.Remove(key);
                else _cache.Put(key, work, work.Bytes, final ? 1 : 2);
            }
            var statistics = Statistics(key, work);
            var content = !final ? SpatialPreviewContentState.Unknown
                : statistics.Readable > 0 || statistics.Pending > 0 ? SpatialPreviewContentState.HasData
                : statistics.NoVisibleSurface > 0 ? SpatialPreviewContentState.NoVisibleSurface
                : statistics.Failed > 0 || statistics.Unsupported > 0 ? SpatialPreviewContentState.Unknown : SpatialPreviewContentState.NoData;
            return new(256, 256, work.Pixels.ToArray(), states.Select(Diagnostic).ToArray())
                { IsFinal = final, ContentState = content, Statistics = statistics };
        }
        finally { Monitor.Exit(work.Gate); }
    }

    private void DrawRegion(WorldDescriptor world, DimensionDescriptor dimension, TileKey key, TileWork work, RegionKey region, CancellationToken token)
    {
        AnvilRegionIndex header;
        var path = dimension.Regions[region];
        try
        {
            lock (_sync) header = _cache.Get<AnvilRegionIndex>(new HeaderKey(path))!;
            if (header is null)
            {
                header = AnvilReader.ReadRegionIndex(world.SourceRoot, path);
                lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); _cache.Put(new HeaderKey(path), header, header.Header.Length + 1280); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            work.HeaderErrors.Add(ex is EndOfStreamException or InvalidDataException ? ChunkReadState.Corrupt : ChunkReadState.Unavailable);
            if (key.Units < 16) return; // Near view retries individual chunks instead of freezing the overview error.
            work.CoarseCounts[5] += 1024;
            for (var slot = 0; slot < 1024; slot++) Accumulate(work, key, region.X * 512 + slot % 32 * 16, region.Z * 512 + slot / 32 * 16, 0xFFAA5656);
            return;
        }
        for (var slot = work.RegionSlot; slot < 1024; slot++)
        {
            token.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32BigEndian(header.Header.AsSpan(slot * 4, 4)) == 0)
            {
                if (key.Units >= 16)
                {
                    work.CoarseCounts[2]++;
                    if (key.Overlay) Accumulate(work, key, region.X * 512 + slot % 32 * 16, region.Z * 512 + slot / 32 * 16, NoDataColor(slot % 32, slot / 32));
                }
                work.RegionSlot = slot + 1; continue;
            }
            var chunk = new ChunkKey(region.X * 32 + slot % 32, region.Z * 32 + slot / 32);
            if (key.Units >= 16)
            {
                uint color = key.Overlay ? 0xFF65798D : 0xFF567B68;
                var encoded = (byte)0;
                if (!header.Valid[slot])
                {
                    encoded = (byte)((int)ChunkReadState.Corrupt + 1);
                    color = StateColor(ChunkReadState.Corrupt, chunk.X, chunk.Z);
                    work.HeaderErrors.Add(ChunkReadState.Corrupt);
                }
                else lock (_sync)
                    if (_cache.Get<Sample>(new CacheKey(key.Generation, key.Layer, chunk, key.Height, key.Mode)) is { } sample)
                    {
                        Rebuild(key, chunk, sample); encoded = Encode(sample.Thumbnail);
                        color = SampleColor(key, sample.Thumbnail, 16, 0, 0);
                        if (sample.Thumbnail.State is not (ChunkReadState.Present or ChunkReadState.Absent)) work.HeaderErrors.Add(sample.Thumbnail.State);
                    }
                Count(work.CoarseCounts, encoded);
                Accumulate(work, key, chunk.X * 16, chunk.Z * 16, color);
            }
            else FillChunk(work.Pixels, key, chunk, (_, _) => key.Overlay ? 0xFF65798D : 0xFF567B68);
            work.RegionSlot = slot + 1;
        }
        if (key.Units < 16) work.HeaderErrors.Clear();
    }
    private static void Accumulate(TileWork work, TileKey key, int bx, int bz, uint color)
    {
        var x = (int)Math.Floor((bx - (double)key.X) / key.Units); var z = (int)Math.Floor((bz - (double)key.Z) / key.Units);
        if (x is < 0 or >= 256 || z is < 0 or >= 256) return;
        var index = z * 256 + x; var divisor = (uint)(key.Units / 16 * (key.Units / 16)); uint result = 0;
        for (var channel = 0; channel < 4; channel++)
        {
            work.Sums![channel][index] += color >> (channel * 8) & 255;
            result |= Math.Min(255, (work.Sums[channel][index] + divisor / 2) / divisor) << (channel * 8);
        }
        Put(work.Pixels, x, z, result);
    }
    private void Rebuild(TileKey key, ChunkKey chunk, Sample sample)
    {
        SurfaceThumbnail? Get(int x, int z) => _cache.Get<Sample>(new CacheKey(key.Generation, key.Layer, new(x, z), key.Height, key.Mode))?.Thumbnail;
        sample.Thumbnail.Rebuild(Get(chunk.X - 1, chunk.Z), Get(chunk.X + 1, chunk.Z), Get(chunk.X, chunk.Z - 1), Get(chunk.X, chunk.Z + 1));
    }
    private void ShadeAndPaint(TileKey key, TileWork work, ChunkKey chunk, Sample sample)
    {
        if (chunk.X * 16d < key.X || chunk.Z * 16d < key.Z || chunk.X * 16d >= key.X + 256d * key.Units || chunk.Z * 16d >= key.Z + 256d * key.Units) return;
        Rebuild(key, chunk, sample);
        FillChunk(work.Pixels, key, chunk, (x, z) => SampleColor(key, sample.Thumbnail, key.Units, x, z));
    }
    private static byte Encode(SurfaceThumbnail sample) => (byte)((int)sample.State + 1
        | (sample.State == ChunkReadState.Present && !sample.HasSurface ? 32 : 0) | (sample.MissingColor ? 64 : 0));
    private static void Count(int[] counts, byte value)
    {
        if ((value & 15) == 0) { counts[0]++; return; }
        var state = (ChunkReadState)((value & 15) - 1);
        if (state == ChunkReadState.Present) { counts[(value & 32) != 0 ? 3 : 1]++; if ((value & 64) != 0) counts[6]++; }
        else if (state == ChunkReadState.Absent) counts[2]++;
        else if (state is ChunkReadState.UnsupportedVersion or ChunkReadState.UnsupportedCompression) counts[4]++;
        else counts[5]++;
    }
    private static SpatialPreviewStatistics Statistics(TileKey key, TileWork work)
    {
        var counts = key.Units >= 16 ? work.CoarseCounts : new int[7];
        if (key.Units < 16) foreach (var value in work.States) Count(counts, value);
        return new(counts[1], counts[0], counts[2], counts[3], counts[4], counts[5], counts[6]);
    }
    private static uint NoDataColor(int x, int z) => ((x + z) & 3) == 0 ? 0xFF42464C : 0xFF555C65;
    private static uint SampleColor(TileKey key, SurfaceThumbnail sample, int units, int x, int z)
    {
        if (sample.State is not (ChunkReadState.Present or ChunkReadState.Absent)) return StateColor(sample.State, x, z);
        if (sample.State == ChunkReadState.Absent) return key.Overlay ? NoDataColor(x, z) : 0;
        var color = sample.Pixel(units, x, z);
        if (!key.Overlay) return color;
        if (!sample.HasSurface) return ((x + z) & 3) == 0 ? 0xFF717985 : 0xFF535963;
        if (sample.MissingColor) return ((x + z) & 3) == 0 ? 0xFFA8A8A8 : color;
        return 0xFF000000 | ((color >> 16 & 255) * 7 + 91) / 8 << 16
            | ((color >> 8 & 255) * 7 + 152) / 8 << 8 | ((color & 255) * 7 + 116) / 8;
    }
    private static void FillChunk(byte[] pixels, TileKey key, ChunkKey chunk, Func<int, int, uint> color)
    {
        var left = (chunk.X * 16 - key.X) / key.Units; var top = (chunk.Z * 16 - key.Z) / key.Units; var size = 16 / key.Units;
        for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
            if (left + x is >= 0 and < 256 && top + z is >= 0 and < 256) Put(pixels, left + x, top + z, color(x, z));
    }
    private static ChunkKey[] Neighbors(ChunkKey key) => [new(key.X - 1, key.Z), new(key.X + 1, key.Z), new(key.X, key.Z - 1), new(key.X, key.Z + 1)];
    private static bool IsPowerOfTwo(double value) => value == Math.Truncate(value) && (((int)value & ((int)value - 1)) == 0);
}
