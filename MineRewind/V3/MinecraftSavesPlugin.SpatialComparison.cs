using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;
using MineRewind.World.Semantic;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : ISpatialComparisonCapability
{
    ConfigKindRef ISpatialComparisonCapability.Kind => MinecraftKind;
    ValueTask<SpatialComparisonDescription> ISpatialComparisonCapability.DescribeComparisonAsync(SpatialComparisonRequest request, PluginInvocationContext context)
        => ValueTask.FromResult(new SpatialComparisonDescription([
            new("blocks", SpatialCategoryText("Block states", "方块状态"), true),
            new("block_entities", SpatialCategoryText("Block entities", "方块实体"), true),
            new("biomes", SpatialCategoryText("Biomes", "生物群系"), true),
            new("other", SpatialCategoryText("Other chunk data", "其他区块数据"), false),
            new("entities", SpatialCategoryText("Entities", "实体"), false),
            new("poi", SpatialCategoryText("Points of interest", "兴趣点"), false),
            new("container", SpatialCategoryText("Storage format", "存储格式"), false),
            new("unclassified", SpatialCategoryText("Unclassified", "未分类"), true)]));
    private static LocalizedText SpatialCategoryText(string en, string zh) => new(en, new Dictionary<string, string> { ["zh-CN"] = zh });
    private readonly Dictionary<Guid, SpatialComparisonState> _spatialComparisons = [];
    private readonly object _spatialComparisonGate = new();
    async ValueTask<SpatialComparisonPage> ISpatialComparisonCapability.CompareAsync(SpatialComparisonRequest request, PluginInvocationContext context)
    {
        EnsureActivated();
        if (request.ComparisonId == Guid.Empty || request.Count is < 1 or > 500 || request.Left.FixedInput is null || request.Right.FixedInput is null)
            throw new ArgumentException("Spatial comparison requires fixed inputs and a bounded page.");
        SpatialComparisonState state;
        lock (_spatialComparisonGate)
        {
            if (!_spatialComparisons.TryGetValue(request.ComparisonId, out state!))
            {
                if (request.Cursor is not null || _spatialComparisons.Count >= 8) throw new InvalidOperationException("Spatial comparison unavailable.");
                _spatialComparisons.Add(request.ComparisonId, state = new(request));
            }
        }
        return await state.NextAsync(request, context.OperationCancellation).ConfigureAwait(false);
    }
    ValueTask ISpatialComparisonCapability.CloseComparisonAsync(Guid id, PluginInvocationContext context)
    {
        lock (_spatialComparisonGate) if (_spatialComparisons.Remove(id, out var state)) state.Close();
        return ValueTask.CompletedTask;
    }
    private void ClearSpatialComparisons()
    {
        lock (_spatialComparisonGate) { foreach (var state in _spatialComparisons.Values) state.Close(); _spatialComparisons.Clear(); }
    }
}

internal sealed class SpatialComparisonState(SpatialComparisonRequest initial)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HashSet<string>[]? _inventories;
    private string[] _paths = [], _worlds = [];
    private readonly HashSet<string> _externalOwners = new(StringComparer.Ordinal);
    private readonly Queue<SpatialChange> _pending = [];
    private int _position, _page;
    private readonly SpatialPreviewSource[] _sources = [initial.Left, initial.Right];
    internal void Close() => _lifetime.Cancel();
    internal async Task<SpatialComparisonPage> NextAsync(SpatialComparisonRequest request, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (request.Left.FixedInput != initial.Left.FixedInput || request.Right.FixedInput != initial.Right.FixedInput
                || request.Cursor != (_page == 0 ? null : _page.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("Spatial comparison input or cursor changed.");
            if (_inventories is null)
            {
                _inventories = _sources.Select(s => SpatialWorldCatalog.Inventory(SpatialWorldCatalog.Root(s), token).ToHashSet(StringComparer.Ordinal)).ToArray();
                _paths = _inventories.SelectMany(i => i).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                _worlds = SpatialWorldCatalog.Worlds(_paths);
                foreach (var path in _paths.Where(p => p.EndsWith(".mcc", StringComparison.OrdinalIgnoreCase)))
                {
                    var slash = path.LastIndexOf('/'); var parts = path[(slash + 1)..].Split('.');
                    if (parts.Length == 4 && parts[0] == "c"
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                        && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z))
                        _externalOwners.Add(path[..(slash + 1)] + FormattableString.Invariant($"r.{(int)Math.Floor(x / 32d)}.{(int)Math.Floor(z / 32d)}.mca"));
                }
                if (initial.InitialPriority is { } priority)
                    _paths = _paths.OrderBy(path =>
                    {
                        if (!MergeRegion.TryAddress(path, _worlds, out var a) || a!.WorldId != priority.WorldId) return 3;
                        if (a.DimensionId != priority.LayerId) return 2;
                        var b = priority.Bounds;
                        return b is null || a.X * 512d < b.MaxX && (a.X + 1d) * 512 > b.MinX && a.Z * 512d < b.MaxY && (a.Z + 1d) * 512 > b.MinY ? 0 : 1;
                    }).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            }
            var rows = new List<SpatialChange>(); var scanned = 0;
            while (rows.Count < request.Count && (_pending.Count > 0 || _position < _paths.Length))
            {
                token.ThrowIfCancellationRequested();
                if (_pending.TryDequeue(out var row)) { rows.Add(row); continue; }
                // Bound even pages containing no changes; the Host can report progress and reprioritize UI work.
                if (scanned++ >= 16) break;
                await CompareFileAsync(_paths[_position++], token).ConfigureAwait(false);
            }
            _page++;
            return new(rows.AsReadOnly(), _pending.Count == 0 && _position == _paths.Length ? null : _page.ToString(CultureInfo.InvariantCulture), _position);
        }
        finally { _gate.Release(); }
    }
    private async Task CompareFileAsync(string path, CancellationToken token)
    {
        var present = _inventories!.Select(i => i.Contains(path)).ToArray();
        string?[] hashes = new string?[2];
        long?[] lengths = new long?[2];
        for (var side = 0; side < 2; side++)
        {
            if (!present[side] && _sources[side].FixedInput?.Coverage?.InspectPath(path) != SpatialPathPresence.Absent)
            { Emit("coverage", SpatialChangeKind.Unavailable, null, [], "Outside proven capture coverage", "不在已证明的捕获范围内"); return; }
            if (present[side])
            {
                await using var stream = Open(side, path);
                lengths[side] = stream.Length;
                hashes[side] = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            }
        }
        var hasExternal = _externalOwners.Contains(path);
        // Identical empty POI placeholders contain no changed record. Do not turn a
        // structural health observation into a 512×512 spatial difference.
        if (!hasExternal && lengths[0] == 0 && lengths[1] == 0
            && MergeRegion.TryAddress(path, _worlds, out var emptyAddress) && emptyAddress!.Component == ChunkComponentKind.Poi) return;
        if (hashes[0] == hashes[1] && !hasExternal && path.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)
            && MergeRegion.TryAddress(path, _worlds, out _))
            hasExternal = await RequiresSemanticReadAsync(path, token).ConfigureAwait(false);
        if (hashes[0] == hashes[1] && !hasExternal) return;
        if (path.EndsWith(".mca", StringComparison.OrdinalIgnoreCase) && MergeRegion.TryAddress(path, _worlds, out var address))
        {
            var region = new SpatialSubjectLocation(address!.WorldId, address.DimensionId,
                new(address.X * 512d, address.Z * 512d, (address.X + 1d) * 512, (address.Z + 1d) * 512));
            try
            {
                var indices = new MergeRegionIndex?[2];
                for (var side = 0; side < 2; side++) if (present[side])
                {
                    var capturedSide = side;
                    indices[side] = await MergeRegion.ReadAsync(path, address,
                        (name, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(Open(capturedSide, name)); }, _inventories![side], token).ConfigureAwait(false);
                }
                var count = 0;
                var chunks = indices.Select(i => i?.Chunks.ToDictionary(c => c.Slot) ?? new Dictionary<int, MergeChunk>()).ToArray();
                foreach (var slot in chunks.SelectMany(c => c.Keys).Distinct().Order())
                {
                    var a = chunks[0].GetValueOrDefault(slot); var b = chunks[1].GetValueOrDefault(slot);
                    if (a?.PayloadHash == b?.PayloadHash) continue;
                    var x = (long)address.X * 32 + slot % 32; var z = (long)address.Z * 32 + slot / 32;
                    var categories = address.Component switch
                    {
                        ChunkComponentKind.Entities => new[] { "entities" },
                        ChunkComponentKind.Poi => new[] { "poi" },
                        _ => Classify(a, b, new((int)x, (int)z))
                    };
                    var kind = a is null ? SpatialChangeKind.Added : b is null ? SpatialChangeKind.Deleted
                        : categories.SequenceEqual(new[] { "container" }) ? SpatialChangeKind.ContainerOnly : SpatialChangeKind.Modified;
                    Emit(slot.ToString(CultureInfo.InvariantCulture), kind,
                        new(address.WorldId, address.DimensionId, new(x * 16d, z * 16d, (x + 1d) * 16, (z + 1d) * 16)), categories,
                        $"Chunk ({x}, {z})", $"区块（{x}, {z}）"); count++;
                }
                if (count == 0 && hashes[0] != hashes[1]) Emit("container", SpatialChangeKind.ContainerOnly, region, ["container"],
                    "Storage format changed; decoded chunk contents are equal", "存储格式变化；解码后的区块内容相同");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or OverflowException or fNbt.NbtFormatException)
            { Emit("unavailable", SpatialChangeKind.Unavailable, region, ["unclassified"], "Cannot compare region: " + ex.Message, "无法比较区域：" + ex.Message,
                $"Unavailable region ({address.X}, {address.Z})", $"无法比较的区域（{address.X}, {address.Z}）"); }
        }
        else Emit("file", !present[0] ? SpatialChangeKind.Added : !present[1] ? SpatialChangeKind.Deleted : SpatialChangeKind.Modified,
            null, [], "File bytes changed", "文件字节变化");

        string[] Classify(MergeChunk? a, MergeChunk? b, ChunkKey key)
        {
            ChunkContentFingerprint? Read(int side, MergeChunk? chunk)
            {
                if (chunk is null) return null;
                var root = SpatialWorldCatalog.Root(_sources[side]);
                var record = AnvilReader.Read(root, Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), key, token);
                return record.Nbt is null ? null : ChunkContentFingerprint.Read(record.Nbt, key, token);
            }
            var left = Read(0, a); var right = Read(1, b);
            if (a is not null && left is null || b is not null && right is null) return ["unclassified"];
            if (a is null || b is null) return ["blocks", "block_entities", "biomes", "other"];
            var categories = new List<string>();
            if (left!.Blocks != right!.Blocks) categories.Add("blocks");
            if (left.BlockEntities != right.BlockEntities) categories.Add("block_entities");
            if (left.Biomes != right.Biomes) categories.Add("biomes");
            if (left.Other != right.Other) categories.Add("other");
            return categories.Count == 0 ? ["container"] : categories.ToArray();
        }
        void Emit(string key, SpatialChangeKind kind, SpatialSubjectLocation? location, string[] components, string en, string zh, string? nameEn = null, string? nameZh = null)
            => _pending.Enqueue(new(path + "#" + key, Text(nameEn ?? (location is null ? path : en), nameZh ?? (location is null ? path : zh)), kind, location, components, Text(en, zh))
                { SourcePaths = [path] });
    }
    private FileStream Open(int side, string relative)
    {
        if (!_inventories![side].Contains(relative)) throw new FileNotFoundException("Fixed input path is absent.", relative);
        var root = SpatialWorldCatalog.Root(_sources[side]);
        return File.Open(MineRewind.World.WorldPaths.RequireContained(root, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))), FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private async Task<bool> RequiresSemanticReadAsync(string path, CancellationToken token)
    {
        await using var file = Open(0, path);
        if (file.Length < 8192) return true;
        var header = new byte[8192]; await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var index = new MineRewind.World.AnvilRegionIndex(header, file.Length, DateTime.UnixEpoch);
        if (index.Valid.Any(v => !v)) return true;
        var offsets = new List<long>();
        for (var slot = 0; slot < 1024; slot++)
        {
            var location = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(slot * 4));
            if (location != 0) offsets.Add((long)(location >> 8) * 4096);
        }
        foreach (var offset in offsets.Order())
        {
            token.ThrowIfCancellationRequested();
            if (offset < 8192 || offset + 5 > file.Length) return true;
            file.Position = offset + 4;
            var compression = file.ReadByte();
            if (compression < 0 || (compression & 128) != 0) return true;
        }
        return false;
    }
    private static LocalizedText Text(string en, string zh) => new(en, new Dictionary<string, string> { ["en-US"] = en, ["zh-CN"] = zh });
}
