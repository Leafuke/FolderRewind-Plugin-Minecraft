using System.Buffers.Binary;
using fNbt;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    private static void WritePreviewRegion(string path, int count, bool firstBroken = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[(2 + count) * 4096];
        for (var slot = 0; slot < count; slot++)
        {
            var root = new NbtCompound("") { new NbtInt("DataVersion", 4786), new NbtInt("xPos", slot % 32), new NbtInt("zPos", slot / 32) };
            if (!(firstBroken && slot == 0)) root.Add(new NbtList("sections", new[] { new NbtCompound { new NbtByte("Y", 0), new NbtCompound("block_states") {
                new NbtList("palette", new[] { new NbtCompound { new NbtString("Name", "minecraft:stone") } }) } } }));
            var payload = new NbtFile(root).SaveToBuffer(NbtCompression.None);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(slot * 4), (uint)((slot + 2) << 8 | 1));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan((slot + 2) * 4096), payload.Length + 1);
            bytes[(slot + 2) * 4096 + 4] = 3;
            payload.CopyTo(bytes, (slot + 2) * 4096 + 5);
        }
        File.WriteAllBytes(path, bytes);
    }
    private static SpatialPreviewTile CompletePreview(WorldPreviewSession session, SpatialPreviewTileRequest request)
    {
        for (var i = 0; i < 1024; i++)
        {
            var tile = session.Render(request, CancellationToken.None);
            if (tile.IsFinal) return tile;
        }
        throw new AssertFailedException("Preview did not converge.");
    }
    [TestMethod]
    public void MalformedChunkIsIsolatedAndProgressiveTileConverges()
    {
        using var world = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(world.WorldPath, "region", "r.0.0.mca"), 128, true);
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(); session.Describe(source, default);
        var request = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 1, 7);
        var first = session.Render(request, default);
        Assert.IsFalse(first.IsFinal);
        Assert.IsLessThanOrEqualTo(64L, session.DecodedChunks);
        var tile = CompletePreview(session, request);
        Assert.IsTrue(tile.Diagnostics.Any(d => d.Code == "preview.Corrupt"));
        Assert.AreEqual(BlockColors.For("minecraft:stone"), BinaryPrimitives.ReadUInt32LittleEndian(tile.BgraPremultiplied.Span.Slice(16 * 4)));
        Assert.IsLessThanOrEqualTo(128L * 1024 * 1024, session.CacheBytes);
        session.Clear(); Assert.AreEqual(0L, session.CacheBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Render(request, default));
    }
    [TestMethod]
    public void FarOverviewDoesNotDecodeAndReusesAvailableTerrain()
    {
        using var world = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(world.WorldPath, "region", "r.0.0.mca"), 1);
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(); session.Describe(source, default);
        var farRequest = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 16, 7);
        var far = CompletePreview(session, farRequest);
        Assert.AreEqual(0L, session.DecodedChunks);
        Assert.AreEqual(0xFF567B68u, BinaryPrimitives.ReadUInt32LittleEndian(far.BgraPremultiplied.Span));
        CompletePreview(session, farRequest with { UnitsPerPixel = 1 });
        var decoded = session.DecodedChunks;
        far = CompletePreview(session, farRequest);
        Assert.AreEqual(decoded, session.DecodedChunks);
        Assert.AreEqual(BlockColors.For("minecraft:stone"), BinaryPrimitives.ReadUInt32LittleEndian(far.BgraPremultiplied.Span));
    }
    [TestMethod]
    public async Task TemporarilyLockedChunkRecoversAfterBackoff()
    {
        using var world = TemporaryWorld.Create();
        var path = Path.Combine(world.WorldPath, "region", "r.0.0.mca"); WritePreviewRegion(path, 1);
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(); session.Describe(source, default);
        var request = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 1, 7);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var partial = session.Render(request, default);
            Assert.IsFalse(partial.IsFinal);
            Assert.IsFalse(partial.Diagnostics.Any(d => d.Code == "preview.Corrupt"));
        }
        await Task.Delay(550);
        var result = CompletePreview(session, request);
        Assert.HasCount(0, result.Diagnostics);
        Assert.AreEqual(BlockColors.For("minecraft:stone"), BinaryPrimitives.ReadUInt32LittleEndian(result.BgraPremultiplied.Span));
    }
    [TestMethod]
    public async Task ConcurrentInspectSharesDecodeAndCancellationDoesNotReopenSession()
    {
        using var world = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(world.WorldPath, "region", "r.0.0.mca"), 1);
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(); session.Describe(source, default);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => session.Inspect(new(source, "minecraft:overworld", 0, 0, 7), default))));
        Assert.AreEqual(1L, session.DecodedChunks);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => session.Render(new(source, "minecraft:overworld", 0, 0, 1, 7), canceled.Token));
    }
    [TestMethod]
    public void AreaMipRetainsNarrowFeaturesAndTransparentCoverage()
    {
        var columns = new SurfaceColumn?[256];
        for (var z = 0; z < 16; z++) columns[z * 16 + 3] = new("test:road", 7, 0xFFFFFFFF, MissingColor: false);
        var thumbnail = new SurfaceThumbnail(new(new(0, 0), ChunkReadState.Present, 4786, columns));
        var pixel = thumbnail.Pixel(8, 0, 0);
        Assert.IsTrue((pixel >> 24) is > 0 and < 255);
        Assert.AreEqual(pixel >> 24, pixel & 255); // Premultiplied white: no dark halo around transparent samples.
        Assert.AreEqual(0u, thumbnail.Pixel(8, 1, 0));
    }
    [TestMethod]
    public void LargeTileConvergesDespiteSummaryEvictionAndWarmRasterNeedsNoDecode()
    {
        using var world = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(world.WorldPath, "region", "r.0.0.mca"), 1024);
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var request = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 8, 7);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var first = session.Render(request, default); var firstMs = timer.ElapsedMilliseconds;
        Assert.IsFalse(first.IsFinal);
        var final = CompletePreview(session, request); var totalMs = timer.ElapsedMilliseconds;
        var decoded = session.DecodedChunks; timer.Restart();
        var warm = session.Render(request, default);
        Assert.IsTrue(warm.IsFinal);
        Assert.AreEqual(decoded, session.DecodedChunks);
        CollectionAssert.AreEqual(final.BgraPremultiplied.ToArray(), warm.BgraPremultiplied.ToArray());
        Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
        Console.WriteLine($"Synthetic 1024 chunks, 8 units/pixel, 1MiB cache: first={firstMs}ms complete={totalMs}ms warm={timer.ElapsedMilliseconds}ms decodes={decoded} cache={session.CacheBytes}bytes");
    }
}
