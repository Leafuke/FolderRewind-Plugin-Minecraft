using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using fNbt;
using MineRewind.World;

namespace MineRewind.Tests;

[TestClass]
public sealed class WorldReadTests
{
    private string _root = null!;
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "MineRewind.WorldTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }

    [TestMethod]
    [DataRow(-513, -33, -2, 31)]
    [DataRow(-512, -32, -1, 0)]
    [DataRow(-17, -2, -1, 30)]
    [DataRow(-16, -1, -1, 31)]
    [DataRow(-1, -1, -1, 31)]
    [DataRow(0, 0, 0, 0)]
    [DataRow(512, 32, 1, 0)]
    public void CoordinatesPreserveNegativeBoundaries(int block, int chunk, int region, int local)
    {
        var key = Coordinates.ChunkAt(block, block);
        Assert.AreEqual(new ChunkKey(chunk, chunk), key);
        Assert.AreEqual(new RegionKey(region, region), key.Region);
        Assert.AreEqual(local * 33, key.Slot);
    }

    [TestMethod]
    [DataRow(1, false)] [DataRow(2, false)] [DataRow(3, false)]
    [DataRow(1, true)] [DataRow(2, true)] [DataRow(3, true)]
    [DataRow(4, false)] [DataRow(4, true)]
    public void RegionEnvelopeReadsInternalAndExternalRecordsWithoutWriting(int compression, bool external)
    {
        // Independent literal NBT: unnamed compound containing no tags.
        byte[] nbt = [10, 0, 0, 0];
        var path = WriteRegion(nbt, compression, external, validLz4: true);
        var original = SHA256.HashData(File.ReadAllBytes(path));
        var record = AnvilReader.Read(_root, path, new(-1, -1));
        Assert.AreEqual(ChunkReadState.Present, record.State);
        CollectionAssert.AreEqual(nbt, record.Nbt);
        CollectionAssert.AreEqual(original, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.AreEqual(ChunkReadState.Absent, AnvilReader.Read(_root, path, new(-2, -1)).State);
    }

    [TestMethod]
    public void BadExtentsOverlapsAndLz4NeverBecomeEmptyChunks()
    {
        var path = WriteRegion([10, 0, 0, 0], 5, false);
        Assert.AreEqual(ChunkReadState.UnsupportedCompression, AnvilReader.Read(_root, path, new(-1, -1)).State);
        path = WriteRegion([10, 0, 0, 0], 4, false); // Truncated Java LZ4Block, not an unsupported codec.
        Assert.AreEqual(ChunkReadState.Corrupt, AnvilReader.Read(_root, path, new(-1, -1)).State);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4092, 4), 0xFFFFFF01);
        File.WriteAllBytes(path, bytes);
        Assert.AreEqual(ChunkReadState.Corrupt, AnvilReader.Read(_root, path, new(-1, -1)).State);
        path = WriteRegion([10, 0, 0, 0], 3, false);
        bytes = File.ReadAllBytes(path);
        bytes.AsSpan(4092, 4).CopyTo(bytes.AsSpan(0, 4));
        File.WriteAllBytes(path, bytes);
        Assert.AreEqual(ChunkReadState.Corrupt, AnvilReader.Read(_root, path, new(-1, -1)).State);
    }

    [TestMethod]
    [DataRow(2860)] [DataRow(4790)]
    public void ModernSurfaceUsesPaddedLongsSignedYAndHeightLimit(int version)
    {
        var palette = new NbtList("palette", NbtTagType.Compound);
        for (var i = 0; i < 17; i++) palette.Add(new NbtCompound { new NbtString("Name", i switch { 0 => "minecraft:air", 1 => "minecraft:water", 2 => "minecraft:stone", _ => "test:unknown" }) });
        var data = new long[342]; // 5 bits/entry: 12 entries/long; never straddle words.
        data[0] = 1L << 55; // x=11, y=0, z=0
        data[1] = 2; // x=12, y=0, z=0
        data[341] = 16L << 15; // x=15, y=15, z=15
        var root = new NbtCompound("")
        {
            new NbtInt("DataVersion", version), new NbtInt("xPos", -1), new NbtInt("zPos", -1),
            new NbtList("sections", new[] { new NbtCompound { new NbtByte("Y", 255),
                new NbtCompound("block_states") { palette, new NbtLongArray("data", data) } } })
        };
        var raw = new NbtFile(root).SaveToBuffer(NbtCompression.None);
        var record = new ChunkRecord(ChunkReadState.Present, raw);
        var surface = SurfaceReader.Read(new(-1, -1), record, 319);
        Assert.AreEqual(ChunkReadState.Present, surface.State);
        Assert.AreEqual("minecraft:water", surface.Columns[11]!.BlockName);
        Assert.AreEqual(-16, surface.Columns[12]!.Height);
        Assert.AreEqual(BlockColors.For("test:unknown"), surface.Columns[255]!.Argb);
        Assert.AreEqual(-1, surface.Columns[255]!.Height);
        Assert.IsNull(SurfaceReader.Read(new(-1, -1), record, -16).Columns[255]);
        root.Get<NbtInt>("DataVersion")!.Value = 5009;
        Assert.AreEqual(ChunkReadState.UnsupportedVersion, SurfaceReader.Read(new(-1, -1),
            new(ChunkReadState.Present, new NbtFile(root).SaveToBuffer(NbtCompression.None)), 319).State);
    }

    [TestMethod]
    public void SinglePaletteAndNamespacedDimensionsHaveNoUiDependency()
    {
        var root = new NbtCompound("") { new NbtInt("DataVersion", 4786), new NbtInt("xPos", 0), new NbtInt("zPos", 0),
            new NbtList("sections", new[] { new NbtCompound { new NbtByte("Y", 0), new NbtCompound("block_states") {
                new NbtList("palette", new[] { new NbtCompound { new NbtString("Name", "minecraft:stone") } }) } } }) };
        var surface = SurfaceReader.Read(new(0, 0), new(ChunkReadState.Present, new NbtFile(root).SaveToBuffer(NbtCompression.None)), 7);
        Assert.IsTrue(surface.Columns.All(c => c?.Height == 7));
        Directory.CreateDirectory(Path.Combine(_root, "dimensions", "example", "nested", "moon", "region"));
        var world = WorldScanner.Scan(_root, _root);
        Assert.AreEqual("example:nested/moon", world.Dimensions.Single().Id);
        Assert.ThrowsExactly<IOException>(() => WorldPaths.RequireContained(_root, Path.GetDirectoryName(_root)!));
    }

    [TestMethod]
    public void InvalidCompressedPayloadAndOversizedNbtStayChunkLocal()
    {
        var path = WriteRegion([10, 0, 0, 0], 3, false);
        var bytes = File.ReadAllBytes(path); bytes[8196] = 2; File.WriteAllBytes(path, bytes);
        Assert.AreEqual(ChunkReadState.Corrupt, AnvilReader.Read(_root, path, new(-1, -1)).State);
        var root = new NbtCompound("") { new NbtInt("DataVersion", 4786), new NbtInt("xPos", -1), new NbtInt("zPos", -1),
            new NbtByteArray("oversized", new byte[1_048_577]) };
        var surface = SurfaceReader.Read(new(-1, -1), new(ChunkReadState.Present, new NbtFile(root).SaveToBuffer(NbtCompression.None)), 7);
        Assert.AreEqual(ChunkReadState.Corrupt, surface.State);
    }

    private string WriteRegion(byte[] nbt, int compression, bool external, bool validLz4 = false)
    {
        using var content = new MemoryStream();
        if (compression is 1 or 2)
        {
            using Stream encoder = compression == 1 ? new GZipStream(content, CompressionLevel.Fastest, true) : new ZLibStream(content, CompressionLevel.Fastest, true);
            encoder.Write(nbt);
        }
        else if (compression == 4 && validLz4)
        {
            // Independent literal Java LZ4Block raw block and terminal block; no production writer.
            var header = new byte[21]; "LZ4Block"u8.CopyTo(header); header[8] = 0x10;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9), nbt.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(13), nbt.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(17), XxHash32.HashToUInt32(nbt, unchecked((int)0x9747b28c)));
            content.Write(header); content.Write(nbt);
            Array.Clear(header, 9, 12); content.Write(header);
        }
        else content.Write(nbt);
        var payload = content.ToArray();
        var file = new byte[12288];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4092, 4), 0x00000201); // (-1,-1), slot 1023
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8192, 4), external ? 1 : payload.Length + 1);
        file[8196] = (byte)(compression | (external ? 128 : 0));
        if (external) File.WriteAllBytes(Path.Combine(_root, "c.-1.-1.mcc"), payload);
        else payload.CopyTo(file, 8197);
        var path = Path.Combine(_root, "r.-1.-1.mca"); File.WriteAllBytes(path, file);
        return path;
    }
}
