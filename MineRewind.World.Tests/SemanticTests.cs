using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

[assembly: DoNotParallelize]
namespace MineRewind.World.Tests;

[TestClass]
public sealed class SemanticTests
{
    private string _root = null!;
    [TestInitialize] public void Init() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WorldSemanticTests", Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    // Literal compound containing an int 'v'. No production writer builds the oracle bytes.
    private static byte[] Nbt(int value) => [10, 0, 0, 3, 0, 1, (byte)'v', (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value, 0];

    private static string Region(string root, int value = 1, int compression = 3, bool external = false,
        string relative = "region/r.-1.-1.mca", int slot = 1023, uint timestamp = 42, byte[]? raw = null)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] payload;
        using (var packed = new MemoryStream())
        {
            if (compression is 1 or 2)
            {
                using (Stream encoder = compression == 1 ? new GZipStream(packed, CompressionLevel.Fastest, true)
                    : new ZLibStream(packed, CompressionLevel.Fastest, true)) encoder.Write(raw ?? Nbt(value));
            }
            else packed.Write(raw ?? Nbt(value));
            payload = packed.ToArray();
        }
        var file = new byte[6 * 4096 + 17];
        // Independent nonzero unallocated sectors and padding; non-contiguous layout and unaligned tail.
        for (var i = 8192; i < file.Length; i++) file[i] = (byte)(i * 31 + 7);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(slot * 4), 0x00000401);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4096 + slot * 4), timestamp);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(4 * 4096), external ? 1 : payload.Length + 1);
        file[4 * 4096 + 4] = (byte)(compression | (external ? 128 : 0));
        if (external)
        {
            var parts = Path.GetFileName(path).Split('.'); var rx = int.Parse(parts[1]); var rz = int.Parse(parts[2]);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, $"c.{rx * 32 + slot % 32}.{rz * 32 + slot / 32}.mcc"), payload);
        }
        else payload.CopyTo(file, 4 * 4096 + 5);
        File.WriteAllBytes(path, file); return path;
    }

    private DirectorySemanticScratch Capture(string source, string name = "scratch", ISemanticScratch? target = null)
    {
        var scratch = target ?? new DirectorySemanticScratch(Path.Combine(_root, name), true);
        WorldCapture.Capture(new DirectoryWorldSource(source), scratch, "source-1");
        return (DirectorySemanticScratch)(target is FaultScratch fault ? fault.Inner : scratch);
    }
    private static Dictionary<string, byte[]> Oracle(string source) => Directory.GetFiles(source, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(source, p).Replace('\\', '/'), File.ReadAllBytes);
    private void CheckRoundTrip(string source, DirectorySemanticScratch scratch)
    {
        var oracle = Oracle(source); Directory.Delete(source, true);
        var output = new DirectorySemanticScratch(Path.Combine(_root, "restored-" + Guid.NewGuid().ToString("N")), true);
        ScratchArchive.Reconstruct(scratch, output);
        var actual = Oracle(output.Root);
        CollectionAssert.AreEquivalent(oracle.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var pair in oracle) CollectionAssert.AreEqual(pair.Value, actual[pair.Key], pair.Key);
    }

    [TestMethod]
    [DataRow(1, false)] [DataRow(2, false)] [DataRow(3, false)]
    [DataRow(1, true)] [DataRow(2, true)] [DataRow(3, true)]
    public void LosslessRoundTripWithoutOriginalInput(int compression, bool external)
    {
        var source = Dir("world"); Region(source, compression: compression, external: external);
        File.WriteAllBytes(Path.Combine(source, "level.dat"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        File.WriteAllBytes(Path.Combine(source, "region", "c.99.99.mcc"), [6, 7]);
        var scratch = Capture(source);
        var summary = ScratchArchive.Validate(scratch).Summary;
        Assert.AreEqual(0, summary.OpaqueRegions); Assert.AreEqual(1, summary.ComparableChunks);
        Assert.AreEqual(1, summary.OrphanExternalFiles);
        var stored = ScratchArchive.ReadLines<StoredFile>(scratch, "files.jsonl").Single(f => f.RelativePath.EndsWith(".mca"));
        Assert.AreEqual(FileEncoding.SegmentedRegion, stored.Encoding);
        Assert.IsTrue(stored.Segments.Any(s => s.ObjectPath.EndsWith("residual.bin")));
        if (!external) Assert.IsTrue(stored.Segments.Any(s => s.ObjectPath.EndsWith("payload.bin")));
        CheckRoundTrip(source, scratch);
    }

    [TestMethod]
    public void CompressionDoesNotChangePayloadHashButContainerChangesAreRetained()
    {
        var hashes = new List<string?>(); var raw = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var source = Dir("world" + i); Region(source, compression: i, timestamp: (uint)i);
            var scratch = Capture(source, "scratch" + i);
            var entry = ScratchArchive.ReadLines<RegionEntry>(scratch, "regions.jsonl").Single();
            hashes.Add(entry.Chunks.Single(c => c.State == SemanticState.Present).PayloadHash); raw.Add(entry.FileHash);
            CheckRoundTrip(source, scratch);
        }
        Assert.IsNotNull(hashes[0]); Assert.AreEqual(1, hashes.Distinct().Count()); Assert.AreEqual(3, raw.Distinct().Count());
    }

    [TestMethod]
    [DataRow("truncated")] [DataRow("overlap")] [DataRow("extent")] [DataRow("length")]
    [DataRow("compression")] [DataRow("nbt")] [DataRow("missing")]
    [DataRow("decode_limit")] [DataRow("broken_zlib")]
    public void InvalidRegionsAreOpaqueAndNeverComparable(string failure)
    {
        var source = Dir("world");
        var path = Region(source, compression: failure == "compression" ? 4 : failure == "decode_limit" ? 2 : 3,
            external: failure is "missing" or "decode_limit", raw: failure == "nbt" ? [99, 5] : failure == "decode_limit" ? new byte[16 * 1024 * 1024 + 1] : null);
        var bytes = File.ReadAllBytes(path);
        if (failure == "truncated") bytes = bytes[..4096];
        if (failure == "overlap") bytes.AsSpan(4092, 4).CopyTo(bytes.AsSpan(0, 4));
        if (failure == "extent") BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4092), 0xffffff01);
        if (failure == "length") BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16384), int.MaxValue);
        if (failure == "broken_zlib") bytes[16388] = 2;
        if (failure == "missing") File.Delete(Path.Combine(source, "region", "c.-1.-1.mcc"));
        File.WriteAllBytes(path, bytes);
        var scratch = Capture(source);
        var summary = ScratchArchive.Validate(scratch).Summary;
        Assert.AreEqual(1, summary.OpaqueRegions); Assert.AreEqual(0, summary.ComparableChunks); Assert.IsFalse(summary.InputComplete);
        if (failure == "missing") Assert.AreEqual(1, summary.MissingExternalFiles);
        CheckRoundTrip(source, scratch);
    }

    [TestMethod]
    public void EveryComponentAndNestedWorldHasSeparateIdentity()
    {
        var source = Dir("worlds");
        foreach (var world in new[] { "one", "two" })
        {
            Directory.CreateDirectory(Path.Combine(source, world)); File.WriteAllBytes(Path.Combine(source, world, "level.dat"), []);
            Region(source, relative: world + "/entities/r.-1.-1.mca");
            Region(source, relative: world + "/DIM-1/poi/r.-1.-1.mca");
            Region(source, relative: world + "/dimensions/example/nested/moon/region/r.0.1.mca", slot: 0);
        }
        var scratch = Capture(source); var rows = ScratchArchive.ReadLines<RegionEntry>(scratch, "regions.jsonl").ToArray();
        Assert.HasCount(6, rows); Assert.AreEqual(2, rows.Select(r => r.Address.WorldId).Distinct().Count());
        Assert.IsTrue(rows.Any(r => r.Address.DimensionId == "example:nested/moon"));
        Assert.IsTrue(rows.SelectMany(r => r.Chunks).Any(c => c.X == 0 && c.Z == 32));
        CheckRoundTrip(source, scratch);
    }

    [TestMethod]
    public void ScratchCorruptionAndMissingCompletionAreRejected()
    {
        var source = Dir("world"); Region(source); var scratch = Capture(source);
        var file = ScratchArchive.ReadLines<StoredFile>(scratch, "files.jsonl").Single();
        var blob = Path.Combine(scratch.Root, file.Segments[0].ObjectPath);
        using (var stream = File.OpenWrite(blob)) stream.WriteByte(255);
        Assert.ThrowsExactly<InvalidDataException>(() => ScratchArchive.Reconstruct(scratch, new DirectorySemanticScratch(Path.Combine(_root, "bad-output"), true)));
        File.Delete(Path.Combine(scratch.Root, "complete.json"));
        Assert.ThrowsExactly<FileNotFoundException>(() => ScratchArchive.Validate(scratch));
    }

    [TestMethod]
    [DataRow("../escape")] [DataRow("/absolute")] [DataRow("a/../b")] [DataRow("a\\b")] [DataRow("a:stream")]
    public void TraversalAndAlternateNamesAreRejected(string path)
        => Assert.ThrowsExactly<InvalidDataException>(() => new DirectorySemanticScratch(Dir("scratch"), false).Create(path));

    [TestMethod]
    public void DisjointOutputAndLinksAreEnforced()
    {
        var source = Dir("source");
        Assert.ThrowsExactly<IOException>(() => SemanticPaths.RequireDisjoint(source, source));
        Assert.ThrowsExactly<IOException>(() => SemanticPaths.RequireDisjoint(Path.Combine(source, "out"), source));
        Assert.ThrowsExactly<IOException>(() => SemanticPaths.RequireDisjoint(_root, source));
        Directory.CreateSymbolicLink(Path.Combine(source, "link"), Dir("target"));
        Assert.ThrowsExactly<IOException>(() => new DirectoryWorldSource(source).GetInventory(default));
    }

    [TestMethod]
    public void CancellationAndWriteFailuresNeverPublishCompletion()
    {
        var source = Dir("world"); Region(source);
        var canceled = new DirectorySemanticScratch(Path.Combine(_root, "canceled"), true);
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldCapture.Capture(new DirectoryWorldSource(source), canceled,
            "source-1", new CancellationToken(true)));
        Assert.IsFalse(canceled.Exists("complete.json"));
        var failed = new DirectorySemanticScratch(Path.Combine(_root, "failed"), true);
        Assert.ThrowsExactly<IOException>(() => Capture(source, target: new FaultScratch(failed)));
        Assert.IsFalse(failed.Exists("complete.json"));
    }

    [TestMethod]
    public void ChangingSourceAndExternalPayloadCannotPublishStaleHashes()
    {
        var source = Dir("world"); var path = Region(source);
        var inner = new DirectoryWorldSource(source);
        var changed = new MutatingSource(inner, () => File.AppendAllText(path, "changed"));
        var scratch = new DirectorySemanticScratch(Path.Combine(_root, "scratch"), true);
        Assert.ThrowsExactly<IOException>(() => WorldCapture.Capture(changed, scratch, "source-1"));
        Assert.IsFalse(scratch.Exists("complete.json"));
    }

    [TestMethod]
    [DataRow(1, 2, 1, ComparisonChoice.Ours)]
    [DataRow(1, 1, 2, ComparisonChoice.Theirs)]
    [DataRow(1, 2, 3, ComparisonChoice.Conflict)]
    [DataRow(0, 2, 3, ComparisonChoice.Conflict)]
    [DataRow(1, 2, 0, ComparisonChoice.Conflict)]
    [DataRow(1, 0, 0, ComparisonChoice.Delete)]
    [DataRow(1, 0, 1, ComparisonChoice.Delete)]
    [DataRow(1, 2, 2, ComparisonChoice.Equal)]
    public void ThreeWayRules(int b, int o, int t, ComparisonChoice expected)
    {
        var snapshots = new List<DirectorySemanticScratch>();
        foreach (var (value, i) in new[] { b, o, t }.Select((v, i) => (v, i)))
        { var source = Dir("world" + i); if (value != 0) Region(source, value); snapshots.Add(Capture(source, "scratch" + i)); }
        var decisions = new List<BundleDecision>();
        WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], decisions.Add);
        Assert.AreEqual(expected, decisions.Single().Choice);
    }

    [TestMethod]
    public void EntityMigrationRequiresDimensionReview()
    {
        var snapshots = new List<DirectorySemanticScratch>();
        for (var i = 0; i < 3; i++)
        {
            var source = Dir("world" + i);
            Region(source, relative: "entities/r.0.0.mca", slot: i); // same opaque E migrates A→B and A→C
            snapshots.Add(Capture(source, "scratch" + i));
        }
        var decisions = new List<BundleDecision>();
        var result = WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], decisions.Add);
        Assert.AreEqual(3, result.DimensionReviews); Assert.IsTrue(decisions.All(d => d.Choice == ComparisonChoice.DimensionReview));
        Assert.IsFalse(result.Applicable);
    }

    [TestMethod]
    public void IndependentChangesInsideSameRegionUseBothSides()
    {
        var snapshots = new List<DirectorySemanticScratch>();
        for (var i = 0; i < 3; i++)
        {
            var source = Dir("world" + i);
            var path = Region(source, value: i == 1 ? 10 : 1, relative: "region/r.0.0.mca", slot: 0);
            var bytes = File.ReadAllBytes(path); var second = Nbt(i == 2 ? 20 : 2);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 0x00000501);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(5 * 4096), second.Length + 1);
            bytes[5 * 4096 + 4] = 3; second.CopyTo(bytes, 5 * 4096 + 5);
            File.WriteAllBytes(path, bytes); snapshots.Add(Capture(source, "scratch" + i));
        }
        var decisions = new List<BundleDecision>();
        WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], decisions.Add);
        Assert.AreEqual(ComparisonChoice.Ours, decisions.Single(d => d.X == 0).Choice);
        Assert.AreEqual(ComparisonChoice.Theirs, decisions.Single(d => d.X == 1).Choice);
    }

    [TestMethod]
    public void UnknownInputBlocksInsteadOfBecomingDeletion()
    {
        var snapshots = new List<DirectorySemanticScratch>();
        for (var i = 0; i < 3; i++)
        { var source = Dir("world" + i); Region(source, compression: i == 1 ? 4 : 3); snapshots.Add(Capture(source, "scratch" + i)); }
        var decisions = new List<BundleDecision>();
        WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], decisions.Add);
        Assert.AreEqual(ComparisonChoice.Blocked, decisions.Single().Choice);
    }

    [TestMethod]
    public void ExternalMutationBetweenDecodeAndStorageIsRejected()
    {
        var source = Dir("world"); Region(source, external: true);
        var path = Path.Combine(source, "region", "c.-1.-1.mcc");
        var changing = new ExternalMutatingSource(new DirectoryWorldSource(source), () => File.WriteAllBytes(path, Nbt(9)));
        var scratch = new DirectorySemanticScratch(Path.Combine(_root, "scratch"), true);
        Assert.ThrowsExactly<IOException>(() => WorldCapture.Capture(changing, scratch, "source-1"));
        Assert.IsFalse(scratch.Exists("complete.json"));
    }

    [TestMethod]
    public void EmptyCompoundAndUnknownDataVersionBytesRemainComparable()
    {
        var source = Dir("world"); Region(source, raw: [10, 0, 0, 0]);
        var scratch = Capture(source);
        Assert.AreEqual(1, ScratchArchive.Validate(scratch).Summary.ComparableChunks);
    }

    [TestMethod]
    public void EmptyRegionIsPreservedButNotAssumedAbsent()
    {
        var source = Dir("world"); Directory.CreateDirectory(Path.Combine(source, "poi"));
        File.WriteAllBytes(Path.Combine(source, "poi", "r.0.0.mca"), []);
        var scratch = Capture(source);
        Assert.AreEqual("empty_region_without_header", ScratchArchive.ReadLines<RegionEntry>(scratch, "regions.jsonl").Single().Diagnostics.Single());
        CheckRoundTrip(source, scratch);
    }

    [TestMethod]
    public void ManifestAndTrailingPackCorruptionAreDetectedBeforeComparison()
    {
        var source = Dir("world"); Region(source); var scratch = Capture(source);
        var first = ScratchArchive.ReadLines<StoredFile>(scratch, "files.jsonl").Single().Segments[0];
        File.AppendAllText(Path.Combine(scratch.Root, first.ObjectPath), "extra");
        Assert.ThrowsExactly<InvalidDataException>(() => WorldComparison.Compare(scratch, scratch, scratch, _ => { }));
        File.AppendAllText(Path.Combine(scratch.Root, "regions.jsonl"), "{}");
        Assert.ThrowsExactly<InvalidDataException>(() => ScratchArchive.Validate(scratch));
    }

    [TestMethod]
    public void BadCoverageIsRejectedEvenWithUpdatedManifestDigest()
    {
        var source = Dir("world"); Region(source); var scratch = Capture(source);
        var complete = ScratchArchive.Validate(scratch);
        var file = ScratchArchive.ReadLines<StoredFile>(scratch, "files.jsonl").Single();
        var segments = file.Segments.ToArray(); segments[0] = segments[0] with { FileOffset = 1 };
        var path = Path.Combine(scratch.Root, "files.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(file with { Segments = segments }, SemanticJson.Options) + "\n");
        complete = complete with { FilesHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
        File.WriteAllText(Path.Combine(scratch.Root, "complete.json"), JsonSerializer.Serialize(complete, SemanticJson.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => ScratchArchive.VerifyIntegrity(scratch));
    }

    [TestMethod]
    public void NonRegionConflictsAndDifferentSourcesAreNotSilentlySuccessful()
    {
        var snapshots = new List<DirectorySemanticScratch>();
        for (var i = 0; i < 3; i++)
        { var source = Dir("world" + i); File.WriteAllText(Path.Combine(source, "level.dat"), "value" + i); snapshots.Add(Capture(source, "scratch" + i)); }
        var result = WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], _ => { });
        Assert.AreEqual(1, result.Conflicts);
        var completion = ScratchArchive.Validate(snapshots[2]) with { SourceId = "other" };
        File.WriteAllText(Path.Combine(snapshots[2].Root, "complete.json"), JsonSerializer.Serialize(completion, SemanticJson.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2], _ => { }));
    }

    [TestMethod]
    public void UnknownRegionNamePreventsClaimsOfCompleteSemanticCoverage()
    {
        var source = Dir("world"); Directory.CreateDirectory(Path.Combine(source, "region"));
        File.WriteAllBytes(Path.Combine(source, "region", "unknown.mca"), [1, 2, 3]);
        var scratch = Capture(source);
        Assert.AreEqual(1, ScratchArchive.Validate(scratch).Summary.UnclassifiedRegions);
        Assert.IsGreaterThan(0, WorldComparison.Compare(scratch, scratch, scratch, _ => { }).Blocked);
        CheckRoundTrip(source, scratch);
    }

    [TestMethod]
    public void CancellationDuringWritingAndUnavailableInputRemainIncomplete()
    {
        var source = Dir("world"); Region(source);
        var scratch = new DirectorySemanticScratch(Path.Combine(_root, "scratch"), true);
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldCapture.Capture(new DirectoryWorldSource(source),
            new CancelScratch(scratch, cancel), "source-1", cancel.Token));
        Assert.IsFalse(scratch.Exists("complete.json"));
        var other = new DirectorySemanticScratch(Path.Combine(_root, "other"), true);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => WorldCapture.Capture(new UnavailableSource(new DirectoryWorldSource(source)), other, "source-1"));
        Assert.IsFalse(other.Exists("complete.json"));
    }

    [TestMethod]
    public void FullRegionIndexAndBufferBoundDoNotDependOnWorldPayloadSize()
    {
        var source = Dir("world"); var path = Region(source, relative: "region/r.0.0.mca", slot: 0);
        var bytes = new byte[(1024 + 2) * 4096]; var nbt = Nbt(1);
        for (var slot = 0; slot < 1024; slot++)
        {
            var offset = (slot + 2) * 4096;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(slot * 4), (uint)((slot + 2) << 8 | 1));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), nbt.Length + 1);
            bytes[offset + 4] = 3; nbt.CopyTo(bytes, offset + 5);
        }
        File.WriteAllBytes(path, bytes);
        var scratch = Capture(source);
        Assert.AreEqual(1024, ScratchArchive.Validate(scratch).Summary.ComparableChunks);
        Assert.IsLessThan(256L * 1024 * 1024, ScratchArchive.Validate(scratch).Summary.DecodeAndCopyBufferBoundBytes);
        CheckRoundTrip(source, scratch);
    }

    private sealed class CancelScratch(ISemanticScratch inner, CancellationTokenSource cancellation) : ISemanticScratch
    {
        public Stream Create(string path) { if (path.StartsWith("objects/")) cancellation.Cancel(); return inner.Create(path); }
        public Stream OpenRead(string path) => inner.OpenRead(path);
        public bool Exists(string path) => inner.Exists(path);
    }
    private sealed class UnavailableSource(IReadOnlyWorldSource inner) : IReadOnlyWorldSource
    {
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => inner.GetInventory(token);
        public Stream OpenRead(string path) => throw new UnauthorizedAccessException("Synthetic unavailable file.");
    }

    private sealed class ExternalMutatingSource(IReadOnlyWorldSource inner, Action mutate) : IReadOnlyWorldSource
    {
        private int _reads;
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => inner.GetInventory(token);
        public Stream OpenRead(string path)
        {
            if (path.EndsWith(".mcc") && ++_reads == 2) mutate();
            return inner.OpenRead(path);
        }
    }

    private sealed class MutatingSource(IReadOnlyWorldSource inner, Action mutate) : IReadOnlyWorldSource
    {
        private int _inventories;
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token)
        { if (++_inventories == 2) mutate(); return inner.GetInventory(token); }
        public Stream OpenRead(string relativePath) => inner.OpenRead(relativePath);
    }
    private sealed class FaultScratch(DirectorySemanticScratch inner) : ISemanticScratch
    {
        public DirectorySemanticScratch Inner => inner;
        public Stream Create(string relativePath) => relativePath.StartsWith("objects/") ? throw new IOException("Simulated disk full") : inner.Create(relativePath);
        public Stream OpenRead(string relativePath) => inner.OpenRead(relativePath);
        public bool Exists(string relativePath) => inner.Exists(relativePath);
    }
}
