using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class SemanticMergeTests
{
    [TestMethod]
    public async Task IdenticalEmptyPoiDoesNotCreateSpatialAlarmButChangedOrTruncatedPoiStillDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "MineRewindEmptyPoi", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
        try
        {
            foreach (var directory in new[] { a, b })
            {
                Directory.CreateDirectory(Path.Combine(directory, "poi"));
                File.WriteAllBytes(Path.Combine(directory, "level.dat"), [1]);
                File.WriteAllBytes(Path.Combine(directory, "poi", "r.0.-1.mca"), []);
            }
            Assert.HasCount(0, await Compare());
            File.WriteAllBytes(Path.Combine(b, "poi", "r.0.-1.mca"), Region((0, Nbt(0, -32, 1))));
            Assert.AreEqual(SpatialChangeKind.Unavailable, (await Compare()).Single().Kind);
            foreach (var directory in new[] { a, b }) File.WriteAllBytes(Path.Combine(directory, "poi", "r.0.-1.mca"), [1, 2]);
            Assert.AreEqual(SpatialChangeKind.Unavailable, (await Compare()).Single().Kind);
        }
        finally { Directory.Delete(root, true); }
        async Task<List<SpatialChange>> Compare()
        {
            var kind = new ConfigKindRef(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity);
            var folder = new FolderSnapshot(Guid.NewGuid(), a, "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var config = new ConfigSnapshot("config", new("1"), kind, "config", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            SpatialPreviewSource Input(string path) => new(config, folder, Guid.NewGuid()) { FixedInput = new(path, path, "fixed") { Coverage = new SpatialCoverage() } };
            var request = new SpatialComparisonRequest(Guid.NewGuid(), Input(a), Input(b), null, 500);
            var state = new SpatialComparisonState(request); var rows = new List<SpatialChange>();
            try
            {
                string? cursor = null;
                do { var page = await state.NextAsync(request with { Cursor = cursor }, default); rows.AddRange(page.Changes); cursor = page.NextCursor; } while (cursor is not null);
                return rows;
            }
            finally { state.Close(); }
        }
    }
    [TestMethod]
    public async Task MapOverrideOfOrdinaryAutomaticChunksChangesActualComposedPayloads()
    {
        var b = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 1)))));
        var o = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 2)), (1, Nbt(1, 0, 1)))));
        var t = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 3)))));
        var run = await Analyze(b, o, t);
        Assert.IsTrue(run.Plan.Subjects.All(s => s.CanOverrideAutomatic && s.SpatialLocations.Count == 1));
        var result = await run.Build(MergeDecisionKind.Base, true);
        CollectionAssert.AreEqual(b["region/r.0.0.mca"], result["region/r.0.0.mca"]);
    }

    [TestMethod]
    public async Task ComparisonDetectsExternalOnlyChangeAndDoesNotCallUncoveredAbsenceDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "MineRewindSpatialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            Write(a, World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 1))));
            Write(b, World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 2))));
            var rows = await Compare(a, b, true);
            Assert.IsTrue(rows.Any(r => r.Kind == SpatialChangeKind.Modified && r.Location?.Bounds == new SpatialPreviewBounds(0, 0, 16, 16)));
            File.Delete(Path.Combine(a, "region", "c.0.0.mcc")); File.Delete(Path.Combine(b, "region", "c.0.0.mcc"));
            var missingBoth = await Compare(a, b, true);
            Assert.IsTrue(missingBoth.Any(r => r.Kind == SpatialChangeKind.Unavailable));
            File.WriteAllBytes(Path.Combine(a, "region", "c.0.0.mcc"), Nbt(0, 0, 1));
            File.Delete(Path.Combine(b, "region", "r.0.0.mca")); File.Delete(Path.Combine(b, "region", "c.0.0.mcc"));
            var unknown = await Compare(a, b, false);
            Assert.IsFalse(unknown.Any(r => r.Kind == SpatialChangeKind.Deleted));
            Assert.IsTrue(unknown.Any(r => r.Kind == SpatialChangeKind.Unavailable));
        }
        finally { Directory.Delete(root, true); }
        static void Write(string root, Dictionary<string, byte[]> files)
        { foreach (var (name, bytes) in files) { var path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); } }
        static async Task<List<SpatialChange>> Compare(string a, string b, bool covered)
        {
            var kind = new ConfigKindRef(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity);
            var folder = new FolderSnapshot(Guid.NewGuid(), a, "world", new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var config = new ConfigSnapshot("config", new("1"), kind, "config", [folder], new Dictionary<StateOwnerId, ProviderStateSnapshot>());
            SpatialPreviewSource Input(string root) => new(config, folder, Guid.NewGuid()) { FixedInput = new(root, root, "fixed") { Coverage = covered ? new SpatialCoverage() : null } };
            var request = new SpatialComparisonRequest(Guid.NewGuid(), Input(a), Input(b), null, 1);
            var state = new SpatialComparisonState(request); var rows = new List<SpatialChange>();
            try
            {
                string? cursor = null;
                do { var page = await state.NextAsync(request with { Cursor = cursor }, default); rows.AddRange(page.Changes); cursor = page.NextCursor; } while (cursor is not null);
                return rows;
            }
            finally { state.Close(); }
        }
    }
    private sealed class SpatialCoverage : ISpatialInputCoverage
    { public SpatialPathPresence InspectPath(string path) => SpatialPathPresence.Absent; }
}
