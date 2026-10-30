using System.Buffers.Binary;
using System.Globalization;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

/// <summary>UI-free adapter. Cached surfaces are expendable preview data, never backup or merge authority.</summary>
internal sealed partial class WorldPreviewSession
{
    private readonly object _sync = new();
    private readonly PreviewBudgetCache _cache;
    public WorldPreviewSession(long cacheLimit = 128L * 1024 * 1024) => _cache = new(cacheLimit);
    private readonly Dictionary<WorldKey, WorldDescriptor> _worlds = [];
    private readonly Dictionary<CacheKey, TaskCompletionSource<ChunkSurface>> _inflight = [];
    private bool _closed;
    private sealed record WorldKey(Guid Generation, string Source, string? WorldId, bool Partial);
    private sealed record CacheKey(Guid Generation, string WorldRoot, string Dimension, ChunkKey Chunk, int Height, SurfaceViewMode Mode = SurfaceViewMode.Surface);
    private sealed record RawKey(CacheKey Key);
    internal long CacheBytes { get { lock (_sync) return _cache.Bytes; } }
    internal long DecodedChunks;
    public void Clear() { lock (_sync) { _cache.Clear(); _worlds.Clear(); _closed = true; } }

    private sealed class WorldResolutionException(bool ambiguous) : IOException
    {
        public bool Ambiguous { get; } = ambiguous;
    }
    public SpatialPreviewDescription Describe(SpatialPreviewSource source, CancellationToken token)
    {
        var roots = SpatialWorldCatalog.Worlds(SpatialWorldCatalog.Inventory(SpatialWorldCatalog.Root(source), token));
        var worlds = roots.Select(w => new SpatialWorld(w.Length == 0 ? "." : w, Text(w.Length == 0 ? Path.GetFileName(source.Folder.Path) : w, w.Length == 0 ? Path.GetFileName(source.Folder.Path) : w))).ToArray();
        if (source.WorldId is { } requested && !worlds.Any(w => w.Id == requested))
            return new([], Text("X", "X"), Text("Z", "Z"), []) { Worlds = worlds };
        WorldDescriptor world;
        try { world = GetWorld(source, token); }
        catch (WorldResolutionException ex)
        {
            var reason = ex.Ambiguous ? Text("Multiple world directories found. This partial backup cannot be previewed unambiguously.", "找到多个候选世界目录，无法唯一识别这份部分备份。")
                : Text("No recognizable Java world directory was found. A level.dat or a world/region directory structure is required.", "未找到可识别的 Java 世界目录，需要 level.dat 或世界的 region 目录结构。");
            return new([], Text("X", "X"), Text("Z", "Z"), [new("preview.world-layout", DiagnosticSeverity.Warning, "SpatialPreview",
                MinecraftSavesPlugin.PluginIdentity, reason.Translations)]) { DistanceUnit = Text("blocks", "方块") };
        }
        var layers = world.Dimensions.Select(d =>
        {
            var keys = d.Regions.Keys;
            var bounds = d.Regions.Count == 0 ? new SpatialPreviewBounds(0, 0, 512, 512)
                : new(keys.Min(k => k.X) * 512d, keys.Min(k => k.Z) * 512d, (keys.Max(k => k.X) + 1d) * 512, (keys.Max(k => k.Z) + 1d) * 512);
            var name = d.Id switch { "minecraft:overworld" => Text("Overworld", "主世界"), "minecraft:the_nether" => Text("The Nether", "下界"), "minecraft:the_end" => Text("The End", "末地"), _ => Text(d.Id, d.Id) };
            var (minimum, maximum) = d.Id switch
            {
                "minecraft:overworld" => (-64, 319), "minecraft:the_nether" or "minecraft:the_end" => (0, 255), _ => (-2048, 2047)
            };
            var surface = new SpatialPreviewHeight(minimum, maximum, maximum, Text("Surface scan Y", "地表扫描上限 Y"));
            var cave = new SpatialPreviewHeight(minimum, maximum, d.Id == "minecraft:the_nether" ? 120 : 64, Text("Cave starting Y", "洞穴起点 Y"));
            var slice = new SpatialPreviewHeight(minimum, maximum, 64, Text("Slice Y", "切片 Y"));
            return new SpatialPreviewLayer(d.Id, name, bounds, 16, 512, surface)
            {
                CoordinateBounds = new(-30_000_512, -30_000_512, 30_000_512, 30_000_512),
                ViewModes = [new("surface", Text("Surface", "地表"), surface), new("cave", Text("Cave", "洞穴"), cave) { UseNavigationHeight = true, UseNavigationHeightAsDefault = d.Id != "minecraft:the_nether" },
                    new("slice", Text("Slice", "切片"), slice) { UseNavigationHeight = true }],
                DefaultViewModeId = d.Id == "minecraft:the_nether" ? "cave" : "surface"
            };
        }).ToArray();
        return new(layers, Text("X", "X"), Text("Z", "Z"), [])
        { Worlds = worlds, LayerLabel = Text("Dimension", "维度"), NavigationLabel = Text("Locate player", "定位玩家"), CellLabel = Text("Chunk", "区块"),
            DistanceUnit = Text("blocks", "方块"), SupportsStatusOverlay = true,
            Legend = [new("readable", Text("Read (green)", "已读（绿色）"), 0xFF5B9874),
                new("pending", Text("Not decoded (blue-gray)", "未解码（蓝灰）"), 0xFF65798D),
                new("absent", source.IsPartial ? Text("Not included / ungenerated (gray)", "未包含／未生成（灰色）") : Text("No record (gray)", "无记录（灰色）"), 0xFF555C65),
                new("empty", Text("No visible surface (dark pattern)", "无可见表面（深灰纹）"), 0),
                new("unsupported", Text("Unsupported (yellow pattern)", "不支持（黄色网纹）"), 0xFFB99A54),
                new("failed", Text("Read failed (red pattern)", "读取失败（红色网纹）"), 0xFFAB5656),
                new("missing", Text("Missing color (gray stripes)", "缺色（灰色斜纹）"), 0xFF969696)] };
    }

    public SpatialPreviewNavigation Navigation(SpatialPreviewSource source, CancellationToken token)
    {
        var world = GetWorld(source, token);
        var targets = WorldNavigation.Read(world, token);
        var values = targets.Select(t => new SpatialPreviewTarget(t.Id,
            t.Spawn ? Text("World", "世界") : Text("Saved players", "玩家上次保存位置"),
            t.Spawn ? Text("World spawn", "世界出生点") : t.SinglePlayer ? Text("Singleplayer — last saved", "当前玩家 · 上次保存位置") : Text(t.Name ?? t.Id, t.Name ?? t.Id),
            t.Dimension, t.X, t.Z) { SuggestedHeight = t.Height }).ToArray();
        var single = targets.FirstOrDefault(t => t.SinglePlayer && world.Dimensions.Any(d => d.Id == t.Dimension));
        return new(values, single?.Id ?? targets.FirstOrDefault(t => t.Spawn && world.Dimensions.Any(d => d.Id == t.Dimension))?.Id)
        { QuickTargetId = single?.Id };
    }

    public SpatialPreviewTile Render(SpatialPreviewTileRequest request, CancellationToken token)
        => RenderProgressive(request, token);

    public SpatialPreviewPoint Inspect(SpatialPreviewPointRequest request, CancellationToken token)
    {
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y) || Math.Abs(request.X) > 30_000_512 || Math.Abs(request.Y) > 30_000_512)
            throw new ArgumentException("Point is outside world bounds.");
        var world = GetWorld(request.Source, token);
        var dimension = world.Dimensions.Single(d => d.Id == request.LayerId);
        var x = (int)Math.Floor(request.X); var z = (int)Math.Floor(request.Y);
        var key = Coordinates.ChunkAt(x, z);
        var surface = Surface(world, dimension, request.Source.Generation, key, Math.Clamp(request.MaximumHeight ?? 2047, -2048, 2047), token, ParseMode(request.ViewModeId));
        var fields = new List<FolderMetadataField>
        {
            Field("dimension", "Dimension ID", "维度标识", dimension.Id),
            Field("block", "Block X, Z", "方块 X, Z", $"{x}, {z}"),
            Field("chunk", "Chunk", "区块", $"{key.X}, {key.Z}"),
            Field("region", "Region", "区域", $"{key.Region.X}, {key.Region.Z}"),
            new("status", Text("State", "状态"), StateText(surface.State, request.Source.IsPartial))
        };
        if (surface.DataVersion is { } version) fields.Add(Field("dataVersion", "DataVersion", "数据版本", version.ToString(CultureInfo.InvariantCulture)));
        if (surface.Columns.Count == 256 && surface.Columns[Coordinates.FloorMod(z, 16) * 16 + Coordinates.FloorMod(x, 16)] is { } column)
        {
            fields.Add(Field("surface", "Surface block", "表面方块", column.BlockName));
            fields.Add(Field("biome", "Biome", "生物群系", column.Biome));
            if (column.MissingColor) fields.Add(new("color", Text("Map color", "地图配色"), Text("Missing color", "缺少配色")));
            fields.Add(Field("height", "Surface Y", "表面高度", column.Height.ToString(CultureInfo.InvariantCulture)));
        }
        else if (surface.State == ChunkReadState.Present)
            fields.Add(new("surface", Text("View result", "当前视图结果"), Text("No visible block at this Y or below in this view", "当前视图在该高度没有可见表面或方块")));
        return new(fields.AsReadOnly(), []);
    }

    private WorldDescriptor GetWorld(SpatialPreviewSource source, CancellationToken token)
    {
        var path = SpatialWorldCatalog.Root(source);
        var key = new WorldKey(source.Generation, NormalizeWorldRoot(path), source.WorldId, source.IsPartial);
        lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); if (_worlds.TryGetValue(key, out var cached)) return cached; }
        var resolution = source.WorldId is { } worldId ? (SpatialWorldCatalog.PathFor(path, worldId), false)
            : source.IsPartial ? PreviewWorldLocator.Resolve(path, token) : (WorldLocator.TryResolveWorldPath(path), false);
        var root = resolution.Item1;
        if (root is null && source.WorldId is null)
        {
            var first = SpatialWorldCatalog.Worlds(SpatialWorldCatalog.Inventory(path, token)).FirstOrDefault();
            if (first is not null) root = SpatialWorldCatalog.PathFor(path, first.Length == 0 ? "." : first);
        }
        if (root is null) throw new WorldResolutionException(resolution.Item2);
        var world = WorldScanner.Scan(path, root, token);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _worlds[key] = world;
        }
        return world;
    }

    private ChunkSurface Surface(WorldDescriptor world, DimensionDescriptor dimension, Guid generation, ChunkKey key, int height, CancellationToken token, SurfaceViewMode mode = SurfaceViewMode.Surface)
    {
        if (!dimension.Regions.TryGetValue(key.Region, out var path)) return ChunkSurface.Empty(key, ChunkReadState.Absent);
        var cacheKey = new CacheKey(generation, NormalizeWorldRoot(world.WorldRoot), dimension.Id, key, height, mode);
        TaskCompletionSource<ChunkSurface> flight;
        bool owner;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_cache.Get<ChunkSurface>(new RawKey(cacheKey)) is { } hit) return hit;
            owner = !_inflight.TryGetValue(cacheKey, out flight!);
            if (owner) _inflight[cacheKey] = flight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (!owner)
        {
            try { return flight.Task.WaitAsync(token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return Surface(world, dimension, generation, key, height, token, mode); }
        }
        try
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref DecodedChunks);
            AnvilRegionIndex? index;
            lock (_sync) index = _cache.Get<AnvilRegionIndex>(new HeaderKey(path));
            var value = SurfaceReader.Read(key, AnvilReader.Read(world.SourceRoot, path, key, token, index), height, token, mode);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (value.State is not (ChunkReadState.Changing or ChunkReadState.Unavailable))
                {
                    var size = 256L + value.Columns.Sum(c => c is null ? 8L : 112L + c.BlockName.Length * 2L + c.Biome.Length * 2L);
                    _cache.Put(new RawKey(cacheKey), value, size, 0);
                }
                _inflight.Remove(cacheKey);
                flight.SetResult(value);
            }
            return value;
        }
        catch (Exception ex)
        {
            lock (_sync) _inflight.Remove(cacheKey);
            if (ex is OperationCanceledException) flight.TrySetCanceled(); else { flight.TrySetException(ex); _ = flight.Task.Exception; }
            throw;
        }
    }

    private static string NormalizeWorldRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }

    private static SurfaceViewMode ParseMode(string? id) => id switch
    {
        null or "surface" => SurfaceViewMode.Surface, "cave" => SurfaceViewMode.Cave, "slice" => SurfaceViewMode.Slice,
        _ => throw new ArgumentException("Unsupported preview mode.")
    };
    private static void Put(byte[] pixels, int x, int y, uint color) => BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan((y * 256 + x) * 4, 4), color);
    private static uint StateColor(ChunkReadState state, int x, int y) => state switch
    {
        ChunkReadState.Present or ChunkReadState.Absent => 0,
        ChunkReadState.UnsupportedVersion or ChunkReadState.UnsupportedCompression => ((x / 4 + y / 4) & 1) == 0 ? 0xFFB99A54 : 0xFF71654A,
        _ => ((x / 4 + y / 4) & 1) == 0 ? 0xFFAB5656 : 0xFF693F46
    };
    private static LocalizedText StateText(ChunkReadState state, bool partial = false) => state switch
    {
        ChunkReadState.Present => Text("Readable", "可读取"), ChunkReadState.Absent => partial ? Text("Not included or not generated", "未包含或未生成") : Text("Not generated", "尚未生成"),
        ChunkReadState.UnsupportedVersion => Text("Unsupported data version", "数据版本暂不支持"),
        ChunkReadState.UnsupportedCompression => Text("Unsupported compression", "压缩格式暂不支持"),
        ChunkReadState.Changing => Text("Changed during reading; refresh", "读取期间发生变化，请刷新"),
        ChunkReadState.Unavailable => Text("Unavailable; refresh", "暂时无法读取，请刷新"),
        _ => Text("Invalid or damaged chunk", "区块格式无效或损坏")
    };
    private static PluginDiagnostic Diagnostic(ChunkReadState state) => new("preview." + state, state == ChunkReadState.Corrupt ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "SpatialPreview",
        MinecraftSavesPlugin.PluginIdentity, new Dictionary<string, string> { ["en-US"] = StateText(state).Default, ["zh-CN"] = StateText(state).Translations["zh-CN"] });
    private static FolderMetadataField Field(string key, string en, string zh, string value) => new(key, Text(en, zh), Text(value, value));
    private static LocalizedText Text(string en, string zh) => new(en, new Dictionary<string, string> { ["en-US"] = en, ["zh-CN"] = zh });
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
                _sessions.Add(source.Generation, session = new(Math.Clamp(source.CacheBudgetBytes, 1024 * 1024, 128L * 1024 * 1024)));
        }
        return session.Describe(source, token);
    }
    private WorldPreviewSession Get(SpatialPreviewSource source)
    {
        lock (_gate) return _sessions.TryGetValue(source.Generation, out var session)
            ? session : throw new ObjectDisposedException(nameof(WorldPreviewSession));
    }
    public SpatialPreviewNavigation Navigation(SpatialPreviewSource source, CancellationToken token) => Get(source).Navigation(source, token);
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
