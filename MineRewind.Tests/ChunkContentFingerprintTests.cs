using fNbt;
using MineRewind.World;
using FolderRewind.Plugin.Abstractions;
using System.Buffers.Binary;

namespace MineRewind.Tests;

[TestClass]
public sealed class ChunkContentFingerprintTests
{
    [TestMethod]
    public async Task FixedComparisonPreservesCategoriesAndSourcePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "MineRewindContentComparison", Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right");
        const string relative = "region/r.-1.0.mca";
        try
        {
            Write(left, Chunk(1)); Write(right, Chunk(2));
            var rows = await Compare();
            CollectionAssert.AreEqual(new[] { "other" }, rows.Single().Components.ToArray());
            CollectionAssert.AreEqual(new[] { relative }, rows.Single().SourcePaths.ToArray());
            Assert.AreEqual(new SpatialPreviewBounds(-16, 0, 0, 16), rows.Single().Location!.Bounds);
            var changed = Chunk(2);
            ((NbtList)((NbtCompound)((NbtList)changed["sections"]!)[0])["block_states"]!["palette"]!)[0] = State("minecraft:dirt");
            Write(right, changed);
            rows = await Compare();
            Assert.IsTrue(rows.Single().Components.Contains("blocks"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        void Write(string directory, NbtCompound chunk)
        {
            Directory.CreateDirectory(Path.Combine(directory, "region"));
            var bytes = Bytes(chunk); var region = new byte[12288];
            BinaryPrimitives.WriteUInt32BigEndian(region.AsSpan(31 * 4, 4), 0x201);
            BinaryPrimitives.WriteInt32BigEndian(region.AsSpan(8192, 4), bytes.Length + 1);
            region[8196] = 3; bytes.CopyTo(region, 8197);
            File.WriteAllBytes(Path.Combine(directory, relative), region);
        }
        async Task<List<SpatialChange>> Compare()
        {
            var kind = new ConfigKindRef(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity);
            var folder = new FolderSnapshot(Guid.NewGuid(), left, "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var config = new ConfigSnapshot("config", new("1"), kind, "config", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            SpatialPreviewSource Source(string directory) => new(config, folder, Guid.NewGuid()) { FixedInput = new(directory, directory, "fixed") };
            var request = new SpatialComparisonRequest(Guid.NewGuid(), Source(left), Source(right), null, 500);
            var state = new SpatialComparisonState(request); var result = new List<SpatialChange>();
            try
            {
                string? cursor = null;
                do { var page = await state.NextAsync(request with { Cursor = cursor }, default); result.AddRange(page.Changes); cursor = page.NextCursor; } while (cursor is not null);
                return result;
            }
            finally { state.Close(); }
        }
    }
    [TestMethod]
    public void MetadataIsSeparateFromBlocksAndUndergroundEditsAreIncluded()
    {
        var before = Read(Chunk(1));
        var metadata = Read(Chunk(2));
        Assert.AreEqual(before.Blocks, metadata.Blocks);
        Assert.AreNotEqual(before.Other, metadata.Other);
        var underground = Chunk(1); ((NbtList)((NbtCompound)((NbtList)underground["sections"]!)[0])["block_states"]!["palette"]!)[0] = State("minecraft:dirt");
        Assert.AreNotEqual(before.Blocks, Read(underground).Blocks);
    }
    [TestMethod]
    public void PaletteReorderingAndCompoundOrderingDoNotCreateBlockChanges()
    {
        var original = Chunk(1); var swapped = Chunk(1);
        var a = (NbtCompound)((NbtList)original["sections"]!)[0]; var b = (NbtCompound)((NbtList)swapped["sections"]!)[0];
        a["block_states"] = Packed(false); b["block_states"] = Packed(true);
        Assert.AreEqual(Read(original).Blocks, Read(swapped).Blocks);
        var p = new NbtCompound("Properties") { new NbtString("facing", "north"), new NbtString("waterlogged", "false") };
        var q = new NbtCompound("Properties") { new NbtString("waterlogged", "false"), new NbtString("facing", "north") };
        var stateA = State("minecraft:oak_stairs"); stateA.Add(p);
        var stateB = State("minecraft:oak_stairs"); stateB.Add(q);
        ((NbtList)a["block_states"]!["palette"]!)[0] = stateA;
        ((NbtList)b["block_states"]!["palette"]!)[1] = stateB;
        Assert.AreEqual(Read(original).Blocks, Read(swapped).Blocks);
        q["facing"] = new NbtString("facing", "south");
        Assert.AreNotEqual(Read(original).Blocks, Read(swapped).Blocks);
    }
    [TestMethod]
    public void BlockEntitiesBiomesAndUnknownVersionsRemainDistinct()
    {
        var original = Chunk(1);
        original.Add(new NbtList("block_entities", [new NbtCompound { new NbtString("id", "minecraft:chest"), new NbtInt("items", 1) }]));
        var changed = (NbtCompound)original.Clone();
        ((NbtCompound)((NbtList)changed["block_entities"]!)[0])["items"] = new NbtInt("items", 2);
        Assert.AreNotEqual(Read(original).BlockEntities, Read(changed).BlockEntities);
        Assert.AreEqual(Read(original).Blocks, Read(changed).Blocks);
        var section = (NbtCompound)((NbtList)changed["sections"]!)[0];
        section["biomes"] = new NbtCompound("biomes") { new NbtList("palette", [new NbtString(null, "minecraft:desert")]) };
        Assert.AreNotEqual(Read(original).Biomes, Read(changed).Biomes);
        changed["DataVersion"] = new NbtInt("DataVersion", int.MaxValue);
        Assert.IsNull(ChunkContentFingerprint.Read(Bytes(changed), new(-1, 0)));
    }
    private static NbtCompound State(string id) => new() { new NbtString("Name", id) };
    private static NbtCompound Packed(bool swap)
    {
        var data = new long[256];
        for (var i = 0; i < 4096; i++) data[i / 16] |= (long)((i % 2) ^ (swap ? 1 : 0)) << (i % 16 * 4);
        return new("block_states") { new NbtList("palette", swap ? [State("minecraft:dirt"), State("minecraft:stone")] : [State("minecraft:stone"), State("minecraft:dirt")]), new NbtLongArray("data", data) };
    }
    private static NbtCompound Chunk(long time) => new("")
    {
        new NbtInt("DataVersion", 3465), new NbtInt("xPos", -1), new NbtInt("zPos", 0), new NbtLong("LastUpdate", time),
        new NbtList("sections", [new NbtCompound
        {
            new NbtByte("Y", unchecked((byte)-4)),
            new NbtCompound("block_states") { new NbtList("palette", [State("minecraft:stone")]) },
            new NbtCompound("biomes") { new NbtList("palette", [new NbtString(null, "minecraft:plains")]) }
        }])
    };
    private static byte[] Bytes(NbtCompound root) => new NbtFile(root).SaveToBuffer(NbtCompression.None);
    private static ChunkContentFingerprint Read(NbtCompound root)
        => ChunkContentFingerprint.Read(Bytes(root), new(-1, 0)) ?? throw new AssertFailedException("Expected a supported fingerprint.");
}

