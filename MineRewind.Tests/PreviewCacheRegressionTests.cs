using System.Buffers.Binary;
using fNbt;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public void WorldSwitchWithinOneGenerationUsesTheSelectedDimensionsAndPlayerLocations()
    {
        using var fixture = TemporaryWorld.Create();
        var other = CreateOtherPreviewWorld(fixture.WorldPath);
        var (config, folder) = Snapshots(Path.GetDirectoryName(fixture.WorldPath)!);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid()) { WorldId = "World" };
        var session = new WorldPreviewSession();
        session.Describe(source, default);
        Assert.AreEqual(32d, session.Navigation(source, default).Targets.Single(t => t.Id == "spawn").X);
        Assert.AreEqual(32.25d, session.Navigation(source, default).Targets.Single(t => t.Id == "singleplayer").X);
        var second = source with { WorldId = Path.GetFileName(other) };
        Assert.IsTrue(session.Describe(second, default).Layers.Any(l => l.Id == "minecraft:the_nether"));
        Assert.AreEqual(512d, session.Navigation(second, default).Targets.Single(t => t.Id == "spawn").X);
        Assert.AreEqual(512.25d, session.Navigation(second, default).Targets.Single(t => t.Id == "singleplayer").X);
        Assert.IsFalse(session.Describe(source, default).Layers.Any(l => l.Id == "minecraft:the_nether"));
        Assert.AreEqual(32d, session.Navigation(source, default).Targets.Single(t => t.Id == "spawn").X);
        Assert.AreEqual(32.25d, session.Navigation(source, default).Targets.Single(t => t.Id == "singleplayer").X);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SameGenerationWorldsDoNotShareSurfacesTilesOrInflightDecodes(bool fixedInput)
    {
        using var fixture = TemporaryWorld.Create();
        var other = CreateOtherPreviewWorld(fixture.WorldPath);
        var (config, folder) = Snapshots(Path.GetDirectoryName(fixture.WorldPath)!);
        var first = new SpatialPreviewSource(config, folder, Guid.NewGuid()) { WorldId = "World" };
        var second = first with { WorldId = Path.GetFileName(other) };
        if (fixedInput)
        {
            first = first with { WorldId = ".", FixedInput = new("A", fixture.WorldPath, "digest-a") };
            second = second with { WorldId = ".", FixedInput = new("B", other, "digest-b") };
        }
        var session = new WorldPreviewSession(); session.Describe(first, default); session.Describe(second, default);
        var values = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var source = i % 2 == 0 ? first : second;
            var inspected = session.Inspect(new(source, "minecraft:overworld", 0, 0, 7), default);
            return inspected.Fields.Single(f => f.Key == "surface").Value.Default;
        })));
        for (var i = 0; i < values.Length; i++) Assert.AreEqual(i % 2 == 0 ? "minecraft:stone" : "minecraft:dirt", values[i]);
        Assert.AreEqual(2L, session.DecodedChunks);
        var a = CompletePreview(session, new(first, "minecraft:overworld", 0, 0, 1, 7));
        var b = CompletePreview(session, new(second, "minecraft:overworld", 0, 0, 1, 7));
        Assert.AreNotEqual(BinaryPrimitives.ReadUInt32LittleEndian(a.BgraPremultiplied.Span), BinaryPrimitives.ReadUInt32LittleEndian(b.BgraPremultiplied.Span));
        CollectionAssert.AreEqual(a.BgraPremultiplied.ToArray(), CompletePreview(session, new(first, "minecraft:overworld", 0, 0, 1, 7)).BgraPremultiplied.ToArray());
        session.Clear(); Assert.AreEqual(0L, session.CacheBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Describe(first, default));
    }

    [TestMethod]
    [DataRow(16, 16)]
    [DataRow(32, 0)]
    [DataRow(4096, 0)]
    public void CoarseTileConvergesAcrossManyRegionsWithOneMiBCache(int units, int origin)
    {
        using var fixture = TemporaryWorld.Create();
        for (var z = 0; z < 9; z++) for (var x = 0; x < 9; x++)
            WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{x}.{z}.mca"), x == 0 && z == 0 ? 1 : 0);
        var (config, folder) = Snapshots(fixture.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid()) { CacheBudgetBytes = 1024 * 1024 };
        var request = new SpatialPreviewTileRequest(source, "minecraft:overworld", origin, origin, units, 7) { ShowStatusOverlay = true };
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var first = session.Render(request, default);
        Assert.IsFalse(first.IsFinal, "More than 64 region headers must require another call.");
        Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
        var peak = session.CacheBytes;
        SpatialPreviewTile result = first;
        var passes = 1;
        for (; passes < 64 && !result.IsFinal; passes++)
        {
            result = session.Render(request, default);
            peak = Math.Max(peak, session.CacheBytes);
            Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
        }
        Assert.IsTrue(result.IsFinal, "Coarse refinement must keep its cursor between calls.");
        Assert.AreEqual(0L, session.DecodedChunks);
        CollectionAssert.AreEqual(ExpectedCoarsePixels(units, origin), result.BgraPremultiplied.ToArray(), "Channel averaging and transparent coverage must match the reference sums.");
        var reference = new WorldPreviewSession(); reference.Describe(source, default);
        var expected = CompletePreview(reference, request);
        CollectionAssert.AreEqual(expected.BgraPremultiplied.ToArray(), result.BgraPremultiplied.ToArray());
        Assert.AreEqual(expected.Statistics, result.Statistics);
        Console.WriteLine($"81 regions; units={units}; origin={origin}; passes={passes}; peak cache={peak} bytes; decodes={session.DecodedChunks}");
        session.Clear(); Assert.AreEqual(0L, session.CacheBytes);
    }

    private static string CreateOtherPreviewWorld(string first)
    {
        var other = Path.Combine(Path.GetDirectoryName(first)!, "Other");
        Directory.CreateDirectory(Path.Combine(other, "DIM-1", "region"));
        foreach (var (path, x, block) in new[] { (first, 32, "minecraft:stone"), (other, 512, "minecraft:dirt") })
        {
            var data = new NbtCompound("Data") { new NbtInt("SpawnX", x), new NbtInt("SpawnY", 64), new NbtInt("SpawnZ", 48) };
            data.Add(new NbtCompound("Player") { new NbtString("Dimension", "minecraft:overworld"),
                new NbtList("Pos", new NbtTag[] { new NbtDouble(null, x + 0.25), new NbtDouble(null, 64), new NbtDouble(null, 51.5) }) });
            new NbtFile(new NbtCompound("") { data }).SaveToFile(Path.Combine(path, "level.dat"), NbtCompression.GZip);
            WritePreviewRegion(Path.Combine(path, "region", "r.0.0.mca"), 1, blockName: block);
        }
        return other;
    }

    private static byte[] ExpectedCoarsePixels(int units, int origin)
    {
        var sums = new long[256 * 256 * 4];
        for (var rz = 0; rz < 9; rz++) for (var rx = 0; rx < 9; rx++)
            for (var cz = 0; cz < 32; cz++) for (var cx = 0; cx < 32; cx++)
            {
                var x = (int)Math.Floor((rx * 512 + cx * 16 - origin) / (double)units);
                var z = (int)Math.Floor((rz * 512 + cz * 16 - origin) / (double)units);
                if (x is < 0 or >= 256 || z is < 0 or >= 256) continue;
                var color = rx == 0 && rz == 0 && cx == 0 && cz == 0 ? 0xFF65798Du
                    : ((cx + cz) & 3) == 0 ? 0xFF42464Cu : 0xFF555C65u;
                for (var channel = 0; channel < 4; channel++) sums[(z * 256 + x) * 4 + channel] += color >> (channel * 8) & 255;
            }
        var divisor = (long)(units / 16) * (units / 16);
        return sums.Select(sum => (byte)Math.Min(255, (sum + divisor / 2) / divisor)).ToArray();
    }
}
