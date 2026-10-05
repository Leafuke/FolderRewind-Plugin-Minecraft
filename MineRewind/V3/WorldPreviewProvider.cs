using System.Buffers.Binary;
using System.Globalization;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

/// <summary>UI-free adapter. Cached surfaces are expendable preview data, never backup or merge authority.</summary>
internal sealed class WorldPreviewSession
{
    private const long CacheLimit = 128L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheItem>> _cache = [];
    private readonly LinkedList<CacheItem> _lru = new();
    private readonly Dictionary<Guid, (string Source, WorldDescriptor World)> _worlds = [];
    private long _bytes;
    private bool _closed;
    private sealed record CacheKey(Guid Generation, string Dimension, ChunkKey Chunk, int Height);
    private sealed record CacheItem(CacheKey Key, ChunkSurface Value, long Bytes);

    public void Clear() { lock (_sync) { _cache.Clear(); _lru.Clear(); _worlds.Clear(); _bytes = 0; _closed = true; } }

    public SpatialPreviewDescription Describe(SpatialPreviewSource source, CancellationToken token)
    {
        var world = GetWorld(source, token);
        var layers = world.Dimensions.Select(d =>
        {
            var keys = d.Regions.Keys;
            var bounds = d.Regions.Count == 0 ? new SpatialPreviewBounds(0, 0, 512, 512)
                : new(keys.Min(k => k.X) * 512d, keys.Min(k => k.Z) * 512d, (keys.Max(k => k.X) + 1d) * 512, (keys.Max(k => k.Z) + 1d) * 512);
            var name = d.Id switch { "minecraft:overworld" => Text("Overworld", "主世界"), "minecraft:the_nether" => Text("The Nether", "下界"), "minecraft:the_end" => Text("The End", "末地"), _ => Text(d.Id, d.Id) };
            return new SpatialPreviewLayer(d.Id, name, bounds, 16, 512, new(-2048, 2047,
                d.Id == "minecraft:the_nether" ? 120 : 2047, Text("Maximum scan Y", "最大扫描 Y")));
        }).ToArray();
        return new(layers, Text("X", "X"), Text("Z", "Z"), []);
    }

    public SpatialPreviewTile Render(SpatialPreviewTileRequest request, CancellationToken token)
    {
        if (!double.IsFinite(request.OriginX) || !double.IsFinite(request.OriginY)
            || Math.Abs(request.OriginX) > 31_100_000 || Math.Abs(request.OriginY) > 31_100_000
            || request.UnitsPerPixel is < 1 or > 4096 || request.UnitsPerPixel != Math.Truncate(request.UnitsPerPixel))
            throw new ArgumentException("Invalid world preview coordinates.");
        var world = GetWorld(request.Source, token);
        var dimension = world.Dimensions.Single(d => d.Id == request.LayerId);
        var height = Math.Clamp(request.MaximumHeight ?? 2047, -2048, 2047);
        var pixels = new byte[256 * 256 * 4];
        var states = new HashSet<ChunkReadState>();
        if (request.UnitsPerPixel < 16)
        {
            var local = new Dictionary<ChunkKey, ChunkSurface>();
            var step = (int)request.UnitsPerPixel;
            SurfaceColumn? Column(int bx, int bz)
            {
                var key = Coordinates.ChunkAt(bx, bz);
                if (!local.TryGetValue(key, out var surface))
                    local[key] = surface = Surface(world, dimension, request.Source.Generation, key, height, token);
                return surface.Columns.Count == 256 ? surface.Columns[Coordinates.FloorMod(bz, 16) * 16 + Coordinates.FloorMod(bx, 16)] : null;
            }
            for (var y = 0; y < 256; y++)
            {
                token.ThrowIfCancellationRequested();
                var bz = checked((int)Math.Floor(request.OriginY + y * step));
                // Bound temporary references as well as the shared cache while rendering coarse tiles.
                var row = Coordinates.FloorDiv(bz, 16);
                foreach (var old in local.Keys.Where(k => Math.Abs(k.Z - row) > 1).ToArray()) local.Remove(old);
                for (var x = 0; x < 256; x++)
                {
                    var bx = checked((int)Math.Floor(request.OriginX + x * step));
                    var column = Column(bx, bz);
                    var surface = local[Coordinates.ChunkAt(bx, bz)];
                    states.Add(surface.State);
                    var color = column is null ? StateColor(surface.State, x, y)
                        : SurfaceShading.Color(column, Column(bx - 1, bz), Column(bx + 1, bz), Column(bx, bz - 1), Column(bx, bz + 1));
                    if (column?.MissingColor == true && ((bx + bz) & 3) == 0) color = 0xFF686868;
                    Put(pixels, x, y, color);
                }
            }
        }
        else
        {
            // Far view is an occupancy overview. Never decode every chunk to make a cold thumbnail.
            var span = 256 * request.UnitsPerPixel;
            var minRx = (int)Math.Floor(request.OriginX / 512); var maxRx = (int)Math.Floor((request.OriginX + span - 1) / 512);
            var minRz = (int)Math.Floor(request.OriginY / 512); var maxRz = (int)Math.Floor((request.OriginY + span - 1) / 512);
            foreach (var pair in dimension.Regions)
            {
                token.ThrowIfCancellationRequested();
                if (pair.Key.X < minRx || pair.Key.X > maxRx || pair.Key.Z < minRz || pair.Key.Z > maxRz) continue;
                try
                {
                    var header = AnvilReader.ReadIndex(world.SourceRoot, pair.Value);
                    for (var slot = 0; slot < 1024; slot++)
                    {
                        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4, 4)) == 0) continue;
                        if (request.UnitsPerPixel >= 512)
                        { Fill(pair.Key.X * 512d, pair.Key.Z * 512d, 512, 0xFF567B68); break; }
                        Fill(pair.Key.X * 512d + slot % 32 * 16, pair.Key.Z * 512d + slot / 32 * 16, 16, 0xFF567B68);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { states.Add(ChunkReadState.Unavailable); Fill(pair.Key.X * 512d, pair.Key.Z * 512d, 512, 0xFFAA5656); }
            }
            void Fill(double x, double y, int size, uint color)
            {
                var left = Math.Clamp((int)Math.Floor((x - request.OriginX) / request.UnitsPerPixel), 0, 256);
                var top = Math.Clamp((int)Math.Floor((y - request.OriginY) / request.UnitsPerPixel), 0, 256);
                var right = Math.Clamp((int)Math.Ceiling((x + size - request.OriginX) / request.UnitsPerPixel), 0, 256);
                var bottom = Math.Clamp((int)Math.Ceiling((y + size - request.OriginY) / request.UnitsPerPixel), 0, 256);
                for (var row = top; row < bottom; row++) for (var col = left; col < right; col++) Put(pixels, col, row, color);
            }
        }
        return new(256, 256, pixels, states.Where(s => s is not (ChunkReadState.Present or ChunkReadState.Absent)).Select(Diagnostic).ToArray());
    }

    public SpatialPreviewPoint Inspect(SpatialPreviewPointRequest request, CancellationToken token)
    {
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y) || Math.Abs(request.X) > 30_000_512 || Math.Abs(request.Y) > 30_000_512)
            throw new ArgumentException("Point is outside world bounds.");
        var world = GetWorld(request.Source, token);
        var dimension = world.Dimensions.Single(d => d.Id == request.LayerId);
        var x = (int)Math.Floor(request.X); var z = (int)Math.Floor(request.Y);
        var key = Coordinates.ChunkAt(x, z);
        var surface = Surface(world, dimension, request.Source.Generation, key, Math.Clamp(request.MaximumHeight ?? 2047, -2048, 2047), token);
        var fields = new List<FolderMetadataField>
        {
            Field("block", "Block X, Z", "方块 X, Z", $"{x}, {z}"),
            Field("chunk", "Chunk", "区块", $"{key.X}, {key.Z}"),
            Field("region", "Region", "区域", $"{key.Region.X}, {key.Region.Z}"),
            new("status", Text("State", "状态"), StateText(surface.State))
        };
        if (surface.DataVersion is { } version) fields.Add(Field("dataVersion", "DataVersion", "数据版本", version.ToString(CultureInfo.InvariantCulture)));
        if (surface.Columns.Count == 256 && surface.Columns[Coordinates.FloorMod(z, 16) * 16 + Coordinates.FloorMod(x, 16)] is { } column)
        {
            fields.Add(Field("surface", "Surface block", "表面方块", column.BlockName));
            fields.Add(Field("biome", "Biome", "生物群系", column.Biome));
            if (column.MissingColor) fields.Add(Field("color", "Map color", "地图配色", "Unknown / 缺少配色"));
            fields.Add(Field("height", "Surface Y", "表面高度", column.Height.ToString(CultureInfo.InvariantCulture)));
        }
        return new(fields.AsReadOnly(), []);
    }

    private WorldDescriptor GetWorld(SpatialPreviewSource source, CancellationToken token)
    {
        var path = Path.GetFullPath(source.Folder.Path);
        lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); if (_worlds.TryGetValue(source.Generation, out var cached) && cached.Source == path) return cached.World; }
        var root = WorldLocator.TryResolveWorldPath(path) ?? throw new IOException("Not a uniquely resolved Java world.");
        var world = WorldScanner.Scan(path, root, token);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _worlds[source.Generation] = (path, world);
        }
        return world;
    }

    private ChunkSurface Surface(WorldDescriptor world, DimensionDescriptor dimension, Guid generation, ChunkKey key, int height, CancellationToken token)
    {
        if (!dimension.Regions.TryGetValue(key.Region, out var path)) return ChunkSurface.Empty(key, ChunkReadState.Absent);
        var cacheKey = new CacheKey(generation, dimension.Id, key, height);
        lock (_sync)
            if (_cache.TryGetValue(cacheKey, out var hit)) { _lru.Remove(hit); _lru.AddFirst(hit); return hit.Value.Value; }
        var value = SurfaceReader.Read(key, AnvilReader.Read(world.SourceRoot, path, key, token), height, token);
        // Do not poison a browsing generation with transient failures.
        if (value.State is ChunkReadState.Changing or ChunkReadState.Unavailable) return value;
        var size = 256L + value.Columns.Sum(c => c is null ? 8L : 112L + c.BlockName.Length * 2L + c.Biome.Length * 2L);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_cache.TryGetValue(cacheKey, out var existing)) return existing.Value.Value;
            var node = _lru.AddFirst(new CacheItem(cacheKey, value, size)); _cache.Add(cacheKey, node); _bytes += size;
            while (_bytes > CacheLimit && _lru.Last is { } last) { _bytes -= last.Value.Bytes; _cache.Remove(last.Value.Key); _lru.RemoveLast(); }
        }
        return value;
    }

    private static void Put(byte[] pixels, int x, int y, uint color) => BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan((y * 256 + x) * 4, 4), color);
    private static uint StateColor(ChunkReadState state, int x, int y) => state switch
    {
        ChunkReadState.Present or ChunkReadState.Absent => 0,
        ChunkReadState.UnsupportedVersion or ChunkReadState.UnsupportedCompression => ((x / 4 + y / 4) & 1) == 0 ? 0xFFB99A54 : 0xFF71654A,
        _ => ((x / 4 + y / 4) & 1) == 0 ? 0xFFAB5656 : 0xFF693F46
    };
    private static LocalizedText StateText(ChunkReadState state) => state switch
    {
        ChunkReadState.Present => Text("Readable", "可读取"), ChunkReadState.Absent => Text("Not generated", "尚未生成"),
        ChunkReadState.UnsupportedVersion => Text("Unsupported data version", "数据版本暂不支持"),
        ChunkReadState.UnsupportedCompression => Text("Unsupported compression (including LZ4)", "压缩格式暂不支持（包括 LZ4）"),
        ChunkReadState.Changing => Text("Changed during reading; refresh", "读取期间发生变化，请刷新"),
        ChunkReadState.Unavailable => Text("Unavailable; refresh", "暂时无法读取，请刷新"),
        _ => Text("Invalid or damaged chunk", "区块格式无效或损坏")
    };
    private static PluginDiagnostic Diagnostic(ChunkReadState state) => new("preview." + state, DiagnosticSeverity.Warning, "SpatialPreview",
        MinecraftSavesPlugin.PluginIdentity, new Dictionary<string, string> { ["en-US"] = StateText(state).Default, ["zh-CN"] = StateText(state).Translations["zh-CN"] });
    private static FolderMetadataField Field(string key, string en, string zh, string value) => new(key, Text(en, zh), Text(value, value));
    private static LocalizedText Text(string en, string zh) => new(en, new Dictionary<string, string> { ["zh-CN"] = zh });
}

/// <summary>Only Describe opens a generation; late render calls cannot recreate closed caches.</summary>
internal sealed class WorldPreviewProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WorldPreviewSession> _sessions = [];
    public SpatialPreviewDescription Describe(SpatialPreviewSource source, CancellationToken token)
    {
        WorldPreviewSession session;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (!_sessions.TryGetValue(source.Generation, out session!))
                _sessions.Add(source.Generation, session = new());
        }
        return session.Describe(source, token);
    }
    private WorldPreviewSession Get(SpatialPreviewSource source)
    {
        lock (_gate) return _sessions.TryGetValue(source.Generation, out var session)
            ? session : throw new ObjectDisposedException(nameof(WorldPreviewSession));
    }
    public SpatialPreviewTile Render(SpatialPreviewTileRequest request, CancellationToken token) => Get(request.Source).Render(request, token);
    public SpatialPreviewPoint Inspect(SpatialPreviewPointRequest request, CancellationToken token) => Get(request.Source).Inspect(request, token);
    public void Close(Guid generation)
    {
        lock (_gate) if (_sessions.Remove(generation, out var session)) session.Clear();
    }
    public void Clear()
    {
        lock (_gate) { foreach (var session in _sessions.Values) session.Clear(); _sessions.Clear(); }
    }
}
