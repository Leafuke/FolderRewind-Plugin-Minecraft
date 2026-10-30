using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class IncrementalTests
{
    private static byte[] Nbt(int value) => [10, 0, 0, 3, 0, 1, 118, 0, 0, 0, (byte)value, 0];
    private static byte[] Region(int value = 1, bool external = false)
    {
        var bytes = new byte[4 * 4096 + 17];
        for (var i = 8192; i < bytes.Length; i++) bytes[i] = (byte)(i * 31);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x201);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4096), 44);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8192), external ? 1 : Nbt(value).Length + 1);
        bytes[8196] = (byte)(external ? 131 : 3);
        if (!external) Nbt(value).CopyTo(bytes, 8197);
        return bytes;
    }
    private static WorldIncrementalState Capture(Source input, Objects objects, WorldIncrementalState? previous = null,
        string[]? deleted = null, bool checkpoint = false)
    {
        objects.NextCapture();
        return WorldIncrementalFormat.Capture(input, previous, objects, objects, "source", "boundary",
            input.Bytes.Keys.Where(p => previous?.Files.All(f => f.Path != p) ?? true).ToArray(),
            input.Bytes.Keys.Where(p => previous?.Files.Any(f => f.Path == p) ?? false).ToArray(),
            deleted ?? [], checkpoint, default);
    }
    private static byte[] Restore(WorldIncrementalState state, string path, Objects objects)
    {
        using var file = WorldIncrementalFormat.OpenFile(state.Files.Single(f => f.Path == path), objects, default);
        using var output = new MemoryStream(); file.CopyTo(output); return output.ToArray();
    }

    [TestMethod]
    public void SparseRegionChangeInheritsOtherFilesAndStoresOnlyChangedBytes()
    {
        var objects = new Objects(); var original = Region();
        var first = Capture(new(new() { ["level.dat"] = [1], ["region/r.0.0.mca"] = original, ["other"] = [2, 3] }), objects);
        var input = new Source(new() { ["region/r.0.0.mca"] = Region(2) });
        var second = Capture(input, objects, first);
        CollectionAssert.AreEqual(Region(2), Restore(second, "region/r.0.0.mca", objects));
        Assert.AreEqual(first.Files.Single(f => f.Path == "other").Recipe, second.Files.Single(f => f.Path == "other").Recipe);
        Assert.IsLessThan(original.Length / 2, second.NewDataBytes);
        CollectionAssert.AreEqual(new[] { "region/r.0.0.mca" }, input.ReadPaths.Distinct().ToArray());
    }

    [TestMethod]
    public void AccountingDoesNotOpenUnchangedRecipesOrPayloads()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["region/r.0.0.mca"] = Region(), ["unchanged"] = [4, 5] }), objects);
        var inherited = objects.Bytes.Keys.ToHashSet(); objects.Reads.Clear();
        var second = Capture(new(new() { ["new"] = [7] }), objects, first);
        Assert.IsFalse(objects.Reads.Any(inherited.Contains));
        Assert.AreEqual(first.Files[0].Recipe, second.Files.Single(f => f.Path == first.Files[0].Path).Recipe);
        Assert.IsGreaterThan(0L, second.ReferencedObjectBytes);
    }

    [TestMethod]
    public void ExternalOnlyEditAndDeletionKeepOwnerBytesButUpdateSemanticIndex()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["level.dat"] = [1], ["region/r.0.0.mca"] = Region(external: true), ["region/c.0.0.mcc"] = Nbt(1) }), objects);
        var second = Capture(new(new() { ["region/c.0.0.mcc"] = Nbt(2) }), objects, first);
        var before = first.Files.Single(f => f.Path.EndsWith(".mca")); var after = second.Files.Single(f => f.Path.EndsWith(".mca"));
        Assert.AreEqual(before.Recipe, after.Recipe); Assert.AreNotEqual(before.Index, after.Index);
        CollectionAssert.AreEqual(Region(external: true), Restore(second, before.Path, objects));
        var third = Capture(new(new()), objects, second, ["region/c.0.0.mcc"]);
        var index = WorldIncrementalFormat.Read<RegionEntry>(objects, third.Files.Single(f => f.Path == before.Path).Index!, default);
        Assert.AreEqual(SemanticState.Unavailable, index.Chunks[0].State);
        Assert.IsEmpty(third.DeletedChunks);
    }

    [TestMethod]
    public void OpaqueZeroAndPureDeletionDoNotInventChunkAbsence()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["region/r.0.0.mca"] = Region(), ["region/c.5.6.mcc"] = [8] }), objects);
        var second = Capture(new(new() { ["region/r.0.0.mca"] = [] }), objects, first);
        Assert.IsEmpty(Restore(second, "region/r.0.0.mca", objects)); Assert.IsEmpty(second.DeletedChunks);
        var third = Capture(new(new()), objects, second, ["region/r.0.0.mca"]);
        CollectionAssert.AreEqual(new byte[] { 8 }, Restore(third, "region/c.5.6.mcc", objects));
        Assert.HasCount(1, third.Files);
    }

    [TestMethod]
    public void HeaderPaddingAndTailChangesRemainByteExact()
    {
        var objects = new Objects(); var bytes = Region();
        var first = Capture(new(new() { ["region/r.0.0.mca"] = bytes }), objects);
        var changed = (byte[])bytes.Clone(); changed[4099]++; changed[16000] ^= 0x37; changed[^1] = 22;
        var second = Capture(new(new() { ["region/r.0.0.mca"] = changed }), objects, first);
        CollectionAssert.AreEqual(changed, Restore(second, "region/r.0.0.mca", objects));
        var one = WorldIncrementalFormat.Read<RegionEntry>(objects, first.Files[0].Index!, default);
        var two = WorldIncrementalFormat.Read<RegionEntry>(objects, second.Files[0].Index!, default);
        Assert.AreEqual(one.Chunks[0].PayloadHash, two.Chunks[0].PayloadHash);
        var unpadded = changed[..(8192 + 4 + BinaryPrimitives.ReadInt32BigEndian(changed.AsSpan(8192)))];
        var third = Capture(new(new() { ["region/r.0.0.mca"] = unpadded }), objects, second);
        CollectionAssert.AreEqual(unpadded, Restore(third, "region/r.0.0.mca", objects));
        var three = WorldIncrementalFormat.Read<RegionEntry>(objects, third.Files[0].Index!, default);
        Assert.AreEqual(SemanticState.Present, three.State);
        Assert.AreEqual(one.Chunks[0].PayloadHash, three.Chunks[0].PayloadHash);
    }

    [TestMethod]
    public void CheckpointNeedsNoOldObjectsAndUsesNoUnchangedSourceReads()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["region/r.0.0.mca"] = Region(), ["plain"] = [4, 5] }), objects);
        var oldIds = objects.Bytes.Keys.ToArray();
        var second = Capture(new(new()), objects, first, checkpoint: true);
        foreach (var id in oldIds) objects.Bytes.Remove(id);
        CollectionAssert.AreEqual(Region(), Restore(second, "region/r.0.0.mca", objects));
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, Restore(second, "plain", objects));
    }

    [TestMethod]
    public void ForeignBoundaryAndUnannouncedDeletionAreRejected()
    {
        var objects = new Objects(); var first = Capture(new(new() { ["file"] = [1] }), objects);
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new(new()), objects, first, ["missing"]));
        Assert.ThrowsExactly<InvalidDataException>(() => Capture(new(new()), objects, first with { BoundaryFingerprint = "other" }));
    }

    [TestMethod]
    public void ExternalTransitionsAndOrphansFollowFinalInventory()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["region/r.0.0.mca"] = Region(), ["region/c.0.0.mcc"] = Nbt(9) }), objects);
        var external = Capture(new(new() { ["region/r.0.0.mca"] = Region(external: true) }), objects, first);
        var index = WorldIncrementalFormat.Read<RegionEntry>(objects, external.Files.Single(f => f.Index is not null).Index!, default);
        Assert.IsTrue(index.Chunks[0].External); Assert.AreEqual(SemanticState.Present, index.Chunks[0].State);
        var inline = Capture(new(new() { ["region/r.0.0.mca"] = Region(3) }), objects, external);
        CollectionAssert.AreEqual(Nbt(9), Restore(inline, "region/c.0.0.mcc", objects));
        var deleted = Capture(new(new()), objects, inline, ["region/r.0.0.mca"]);
        CollectionAssert.AreEqual(Nbt(9), Restore(deleted, "region/c.0.0.mcc", objects));
        Assert.HasCount(1, deleted.Files);
    }

    [TestMethod]
    public void UnknownCompressionStaysOpaqueAndCanReturnToNormal()
    {
        var objects = new Objects(); var bytes = Region(); bytes[8196] = 42;
        var first = Capture(new(new() { ["region/r.0.0.mca"] = bytes }), objects);
        var index = WorldIncrementalFormat.Read<RegionEntry>(objects, first.Files[0].Index!, default);
        Assert.AreEqual(SemanticState.Unsupported, index.State);
        CollectionAssert.AreEqual(bytes, Restore(first, "region/r.0.0.mca", objects));
        var second = Capture(new(new() { ["region/r.0.0.mca"] = Region() }), objects, first);
        CollectionAssert.AreEqual(Region(), Restore(second, "region/r.0.0.mca", objects));
        Assert.IsEmpty(second.DeletedChunks);
    }

    [TestMethod]
    public void LargeChangeKeepsUnchangedReferencesWithoutAutomaticRepack()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["large"] = new byte[1000], ["small"] = [1] }), objects);
        var unchanged = first.Files.Single(f => f.Path == "small"); var changed = new byte[1000]; changed[0] = 7;
        var input = new Source(new() { ["large"] = changed });
        var second = Capture(input, objects, first);
        Assert.IsFalse(second.Checkpoint);
        Assert.AreEqual(unchanged.Recipe, second.Files.Single(f => f.Path == "small").Recipe);
        CollectionAssert.AreEqual(changed, Restore(second, "large", objects));
        CollectionAssert.AreEqual(new byte[] { 1 }, Restore(second, "small", objects));
        CollectionAssert.AreEqual(new[] { "large" }, input.ReadPaths.ToArray());
        Assert.AreEqual(1000L, second.NewDataBytes);
    }

    [TestMethod]
    public void CancellationAndUndeclaredInputNeverProduceAState()
    {
        var objects = new Objects(); var canceled = new CancellationToken(true);
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldIncrementalFormat.Capture(
            new Source(new() { ["a"] = [1] }), null, objects, objects, "source", "boundary", ["a"], [], [], false, canceled));
        Assert.ThrowsExactly<InvalidDataException>(() => WorldIncrementalFormat.Capture(
            new Source(new() { ["unannounced"] = [1] }), null, objects, objects, "source", "boundary", [], [], [], false, default));
    }

    [TestMethod]
    public void RecompressionKeepsSemanticHashButPreservesNewCompressedBytes()
    {
        var objects = new Objects();
        byte[] Compressed(CompressionLevel level)
        {
            var bytes = Region(); using var payload = new MemoryStream();
            using (var zlib = new ZLibStream(payload, level, true)) zlib.Write(Nbt(7));
            var encoded = payload.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8192), encoded.Length + 1);
            bytes[8196] = 2; encoded.CopyTo(bytes, 8197); return bytes;
        }
        var before = Compressed(CompressionLevel.Fastest); var after = Compressed(CompressionLevel.SmallestSize);
        var first = Capture(new(new() { ["region/r.0.0.mca"] = before }), objects);
        var second = Capture(new(new() { ["region/r.0.0.mca"] = after }), objects, first);
        var one = WorldIncrementalFormat.Read<RegionEntry>(objects, first.Files[0].Index!, default).Chunks[0];
        var two = WorldIncrementalFormat.Read<RegionEntry>(objects, second.Files[0].Index!, default).Chunks[0];
        Assert.AreEqual(one.PayloadHash, two.PayloadHash);
        Assert.AreNotEqual(one.PayloadStorageHash, two.PayloadStorageHash);
        CollectionAssert.AreEqual(after, Restore(second, "region/r.0.0.mca", objects));
    }

    private sealed class Source(Dictionary<string, byte[]> bytes) : IReadOnlyWorldSource
    {
        public Dictionary<string, byte[]> Bytes => bytes;
        public List<string> ReadPaths { get; } = [];
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => bytes.Select(p => new SourceFile(p.Key, p.Value.Length, DateTime.UnixEpoch)).ToArray();
        public Stream OpenRead(string path) { ReadPaths.Add(path); return new MemoryStream(bytes[path], false); }
    }
    private sealed class Objects : IWorldObjectReader, IWorldObjectWriter
    {
        public Dictionary<Guid, byte[]> Bytes { get; } = [];
        public List<Guid> Reads { get; } = [];
        private readonly Dictionary<Guid, HashSet<Guid>> _dependencies = [];
        private HashSet<Guid> _previous = [];
        public bool Owns(Guid id) => !_previous.Contains(id) && Bytes.ContainsKey(id);
        public long GetPackLength(Guid id) => Bytes[id].LongLength;
        public IReadOnlyCollection<Guid> GetDependencies(Guid id) => _dependencies[id];
        public void BeginRepack() { }
        public void NextCapture() => _previous = Bytes.Keys.ToHashSet();
        public void ValidateDependency(Guid owner, Guid dependency)
            => Assert.Contains(dependency, _dependencies[owner], "Missing declared object dependency.");
        public Stream Open(WorldObjectReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads.Add(reference.ArtifactId); var bytes = Bytes[reference.ArtifactId];
            Assert.AreEqual(reference.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
            return new MemoryStream(bytes, false);
        }
        public WorldObjectReference Store(Stream input, long length, bool metadata, IReadOnlyCollection<Guid> dependencies, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var bytes = new byte[checked((int)length)]; input.ReadExactly(bytes);
            var id = Guid.NewGuid(); Bytes.Add(id, bytes); _dependencies.Add(id, dependencies.ToHashSet());
            return new(id, "pack.bin", 0, length, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }
}
