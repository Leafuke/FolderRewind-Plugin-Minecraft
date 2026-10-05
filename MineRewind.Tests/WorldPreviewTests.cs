using System.Buffers.Binary;
using System.Security.Cryptography;
using fNbt;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task PreviewReadsTilesAndDetailsWithoutCaptureAndRefreshInvalidatesCache()
    {
        using var world = TemporaryWorld.Create();
        var directory = Path.Combine(world.WorldPath, "region"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "r.0.0.mca");
        void Write(string block)
        {
            var nbt = new NbtCompound("") { new NbtInt("DataVersion", 4786), new NbtInt("xPos", 0), new NbtInt("zPos", 0),
                new NbtList("sections", new[] { new NbtCompound { new NbtByte("Y", 0), new NbtCompound("block_states") {
                    new NbtList("palette", new[] { new NbtCompound { new NbtString("Name", block) } }) } } }) };
            var payload = new NbtFile(nbt).SaveToBuffer(NbtCompression.None);
            var bytes = new byte[12288];
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x00000201);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8192, 4), payload.Length + 1);
            bytes[8196] = 3; payload.CopyTo(bytes, 8197); File.WriteAllBytes(path, bytes);
        }
        Write("minecraft:stone");
        var digest = SHA256.HashData(File.ReadAllBytes(path));
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var description = await fixture.Plugin.DescribeAsync(source, fixture.Invocation);
        Assert.AreEqual("minecraft:overworld", description.Layers.Single().Id);
        var tile = await fixture.Plugin.RenderAsync(new(source, "minecraft:overworld", 0, 0, 1, 7), fixture.Invocation);
        Assert.AreEqual(BlockColors.For("minecraft:stone"), BinaryPrimitives.ReadUInt32LittleEndian(tile.BgraPremultiplied.Span));
        var details = await fixture.Plugin.InspectAsync(new(source, "minecraft:overworld", 0, 0, 7), fixture.Invocation);
        Assert.AreEqual("7", details.Fields.Single(f => f.Key == "height").Value.Default);
        CollectionAssert.AreEqual(digest, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.HasCount(0, fixture.Services.Backups.Requests);
        Assert.HasCount(0, fixture.Services.Restores.Requests);
        Assert.HasCount(0, fixture.Services.KnotLink.Events);

        Write("minecraft:water");
        var refreshed = source with { Generation = Guid.NewGuid() };
        await fixture.Plugin.DescribeAsync(refreshed, fixture.Invocation);
        tile = await fixture.Plugin.RenderAsync(new(refreshed, "minecraft:overworld", 0, 0, 1, 7), fixture.Invocation);
        Assert.AreEqual(BlockColors.For("minecraft:water"), BinaryPrimitives.ReadUInt32LittleEndian(tile.BgraPremultiplied.Span));
        // Far view works even when record bytes are corrupt: it only needs the location table.
        var damaged = File.ReadAllBytes(path); damaged[8196] = 4; File.WriteAllBytes(path, damaged);
        var far = await fixture.Plugin.RenderAsync(new(refreshed, "minecraft:overworld", 0, 0, 16, 7), fixture.Invocation);
        Assert.AreEqual(0xFF567B68u, BinaryPrimitives.ReadUInt32LittleEndian(far.BgraPremultiplied.Span));
        Assert.HasCount(0, far.Diagnostics);
    }
}
