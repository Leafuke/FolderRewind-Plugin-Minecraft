using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

[TestClass]
public sealed partial class SemanticMergeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IndependentChunksComposeOriginalPayloadsWithDeterministicPadding(bool omitFinalPadding)
    {
        var b = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 1)))));
        var o = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 2)), (1, Nbt(1, 0, 1)))));
        var t = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 3)))));
        if (omitFinalPadding)
            foreach (var world in new[] { b, o, t }) world["region/r.0.0.mca"] = Unpadded(world["region/r.0.0.mca"]);
        var run = await Analyze(b, o, t);
        Assert.HasCount(2, run.Plan.Subjects); Assert.IsTrue(run.Plan.Subjects.All(s => s.IsAutomatic));
        var result = await run.Build();
        CollectionAssert.AreEqual(Region((0, Nbt(0, 0, 2)), (1, Nbt(1, 0, 3))), result["region/r.0.0.mca"]);
        CollectionAssert.AreEqual(result["region/r.0.0.mca"], (await run.Build())["region/r.0.0.mca"]);
    }

    [TestMethod]
    [DataRow("conservative")]
    [DataRow("terrain-first")]
    public async Task PersistedDefaultCacheExclusionsAllowMergeWithoutAllowingWorldDataExclusions(string policy)
    {
        // These are the actual Host-normalized rules persisted by the default Minecraft FilePolicy.
        var rules = new[] { "**/distanthorizons.sqlite", "**/distanthorizons.sqlite-shm", "**/distanthorizons.sqlite-wal", "**/session.lock", "session.lock", "voxy/**" };
        var boundary = new MergeInputBoundary(1, MergeBoundaryScope.All, [], MergeBoundaryFilter.Blacklist, rules, false, "default-world");
        var b = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 1)))));
        var o = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 2)), (1, Nbt(1, 0, 1)))));
        var t = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)), (1, Nbt(1, 0, 3)))));
        var run = await Analyze(b, o, t, policy, boundary);
        CollectionAssert.AreEqual(Region((0, Nbt(0, 0, 2)), (1, Nbt(1, 0, 3))), (await run.Build())["region/r.0.0.mca"]);
        var blocked = await Analyze(b, o, t, policy, boundary, q => q.RequirementId == "entities" ? MergeCoverageState.Filtered : MergeCoverageState.Complete);
        Assert.IsTrue(blocked.Plan.Subjects.Single().IsBlocked);
        Assert.AreEqual(".", blocked.Plan.Subjects.Single().SpatialLocations.Single().WorldId);
        Assert.IsEmpty(blocked.Plan.Subjects.Single().AllowedDecisions);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => blocked.Build());
        var unknown = await Analyze(b, o, t, policy, boundary, _ => MergeCoverageState.Unknown);
        Assert.IsTrue(unknown.Plan.Subjects.Single().IsBlocked);
    }

    [TestMethod]
    public async Task SameChunkDecisionSupportsBaseDeleteAndExactExternalPayloadReuse()
    {
        var b = World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 1)));
        var o = World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 2)));
        var t = World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 3)));
        var run = await Analyze(b, o, t);
        Assert.IsFalse(run.Plan.Subjects.Single().IsAutomatic);
        var restored = await run.Build(MergeDecisionKind.Base);
        CollectionAssert.AreEqual(b["region/r.0.0.mca"], restored["region/r.0.0.mca"]);
        CollectionAssert.AreEqual(b["region/c.0.0.mcc"], restored["region/c.0.0.mcc"]);
        var deleted = await run.Build(MergeDecisionKind.Delete);
        Assert.IsFalse(deleted.ContainsKey("region/r.0.0.mca")); Assert.IsFalse(deleted.ContainsKey("region/c.0.0.mcc"));
        t["region/c.0.0.mcc"] = [0];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Analyze(b, o, t));
    }

    [TestMethod]
    public async Task OneSidedCrossDimensionEntityMoveGatesWholeWorldAndTerrainFirstKeepsAllOursEntities()
    {
        var terrain = Region((0, Nbt(0, 0, 1))); var entity = Unpadded(Region((0, Nbt(0, 0, 42))));
        var b = World(("region/r.0.0.mca", terrain), ("DIM-1/region/r.0.0.mca", terrain), ("entities/r.0.0.mca", entity));
        var o = World(("region/r.0.0.mca", terrain), ("DIM-1/region/r.0.0.mca", terrain), ("DIM-1/entities/r.0.0.mca", entity));
        var t = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 3)))), ("DIM-1/region/r.0.0.mca", terrain), ("entities/r.0.0.mca", entity));
        var conservative = await Analyze(b, o, t);
        Assert.HasCount(1, conservative.Plan.Groups); Assert.HasCount(1, conservative.Plan.Subjects);
        Assert.IsFalse(conservative.Plan.Subjects[0].IsAutomatic);
        var selected = await conservative.Build(MergeDecisionKind.Theirs);
        Assert.IsTrue(selected.ContainsKey("entities/r.0.0.mca")); Assert.IsFalse(selected.ContainsKey("DIM-1/entities/r.0.0.mca"));
        var terrainFirst = await Analyze(b, o, t, "terrain-first");
        var composed = await terrainFirst.Build();
        Assert.IsFalse(composed.ContainsKey("entities/r.0.0.mca"));
        CollectionAssert.AreEqual(entity, composed["DIM-1/entities/r.0.0.mca"]);
        CollectionAssert.AreEqual(t["region/r.0.0.mca"], composed["region/r.0.0.mca"]);
    }

    [TestMethod]
    public async Task PartialBoundaryAndUnknownTerrainLayoutAreBlockedWithoutTreatingDamageAsDeletion()
    {
        var b = World(("region/r.0.0.mca", Region((0, Nbt(0, 0, 1)))));
        var filtered = new MergeInputBoundary(1, MergeBoundaryScope.All, [], MergeBoundaryFilter.Blacklist, ["entities/**"], false, "filtered");
        var blocked = await Analyze(b, b, b, boundary: filtered, coverage: _ => MergeCoverageState.Filtered);
        Assert.IsTrue(blocked.Plan.Subjects.Single().IsBlocked);
        var old = World(("region/r.0.0.mca", Region((0, new byte[] { 10, 0, 0, 0 }))));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Analyze(old, old, old, "terrain-first"));
        var damaged = World(("region/r.0.0.mca", new byte[] { 0 }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Analyze(b, b, damaged));
        var unchanged = await Analyze(damaged, damaged, damaged);
        CollectionAssert.AreEqual(damaged["region/r.0.0.mca"], (await unchanged.Build())["region/r.0.0.mca"]);
    }

    [TestMethod]
    [DataRow("empty_compound", true)]
    [DataRow("empty_end", true)]
    [DataRow("empty_upper", true)]
    [DataRow("scalar", false)]
    [DataRow("nested", false)]
    [DataRow("legacy", false)]
    [DataRow("wrong_list", false)]
    public async Task TerrainFirstAllowsOnlyKnownEmptyRootEntityListsAndKeepsOursEntities(string shape, bool allowed)
    {
        byte[] Terrain(int left, int right) => Region((0, TerrainNbt(0, 0, left, shape)), (1, TerrainNbt(1, 0, right, shape)));
        var b = World(("region/r.0.0.mca", Terrain(1, 1)), ("entities/r.0.0.mca", Region((0, Nbt(0, 0, 1)))));
        var o = World(("region/r.0.0.mca", Terrain(2, 1)), ("entities/r.0.0.mca", Region((0, Nbt(0, 0, 2)))));
        var t = World(("region/r.0.0.mca", Terrain(1, 3)), ("entities/r.0.0.mca", Region((0, Nbt(0, 0, 3)))));
        if (!allowed)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Analyze(b, o, t, "terrain-first"));
            return;
        }
        var run = await Analyze(b, o, t, "terrain-first");
        Assert.HasCount(2, run.Plan.Subjects);
        Assert.IsTrue(run.Plan.Subjects.All(s => s.IsAutomatic && !s.IsBlocked));
        Assert.IsTrue(run.Plan.Groups.All(g => !g.State.GetProperty("WholeWorld").GetBoolean()));
        var result = await run.Build();
        CollectionAssert.AreEqual(Terrain(2, 3), result["region/r.0.0.mca"], "Selected payloads, including empty lists, remain byte-exact.");
        CollectionAssert.AreEqual(o["entities/r.0.0.mca"], result["entities/r.0.0.mca"]);
        var conservative = await Analyze(b, o, t);
        Assert.IsTrue(conservative.Plan.Groups.Single().State.GetProperty("WholeWorld").GetBoolean(), "Real entity changes still gate the conservative policy.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmbeddedTerrainAndPoiArePinnedToOursIncludingAbsenceWhileOtherChunksMerge(bool absentInOurs)
    {
        var b = World(("region/r.0.0.mca", Region((0, TerrainNbt(0, 0, 1, "populated")), (1, Nbt(1, 0, 1)))),
            ("poi/r.0.0.mca", Region((0, Nbt(0, 0, 10)), (1, Nbt(1, 0, 10)))));
        var o = World(("region/r.0.0.mca", absentInOurs ? Region((1, Nbt(1, 0, 1))) : Region((0, TerrainNbt(0, 0, 2, "mixed")), (1, Nbt(1, 0, 1)))),
            ("poi/r.0.0.mca", absentInOurs ? Region((1, Nbt(1, 0, 10))) : Region((0, Nbt(0, 0, 20)), (1, Nbt(1, 0, 10)))));
        var t = World(("region/r.0.0.mca", Region((0, TerrainNbt(0, 0, 3, "populated")), (1, Nbt(1, 0, 3)))),
            ("poi/r.0.0.mca", Region((0, Nbt(0, 0, 30)), (1, Nbt(1, 0, 30)))));
        var run = await Analyze(b, o, t, "terrain-first");
        var pinned = run.Plan.Subjects.Single(s => s.SubjectId == "0");
        Assert.IsTrue(pinned.IsAutomatic); Assert.IsEmpty(pinned.AllowedDecisions);
        StringAssert.Contains(pinned.DisplayName.Translations["zh-CN"], "固定保留目标分支");
        Assert.AreEqual("1", run.AnalysisResult.Diagnostics.Single().Arguments["count"]);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, run.AnalysisResult.Outcome);
        var result = await run.Build(MergeDecisionKind.Theirs);
        CollectionAssert.AreEqual(absentInOurs ? Region((1, Nbt(1, 0, 3))) : Region((0, TerrainNbt(0, 0, 2, "mixed")), (1, Nbt(1, 0, 3))), result["region/r.0.0.mca"]);
        CollectionAssert.AreEqual(absentInOurs ? Region((1, Nbt(1, 0, 30))) : Region((0, Nbt(0, 0, 20)), (1, Nbt(1, 0, 30))), result["poi/r.0.0.mca"]);
        run.Plan.Subjects[run.Plan.Subjects.IndexOf(pinned)] = pinned with { State = JsonSerializer.SerializeToElement(new { Slot = 0, AutomaticSide = 2 }) };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => run.Build());
    }

    [TestMethod]
    public async Task IncompleteWorldDoesNotPreventAnotherWorldFromBeingAnalyzed()
    {
        Dictionary<string, byte[]> Worlds(int left, int right) => new()
        {
            ["one/level.dat"] = [1], ["two/level.dat"] = [1], ["outside.txt"] = [1],
            ["one/region/r.0.0.mca"] = Region((0, Nbt(0, 0, left))),
            ["two/region/r.0.0.mca"] = Region((0, Nbt(0, 0, right)))
        };
        var run = await Analyze(Worlds(1, 1), Worlds(2, 1), Worlds(1, 2), coverage:
            q => q.RelativeRoot == "one" ? MergeCoverageState.Filtered : MergeCoverageState.Complete);
        Assert.HasCount(1, run.Plan.Subjects.Where(s => s.IsBlocked));
        Assert.HasCount(1, run.Plan.Subjects.Where(s => s.IsAutomatic));
    }

    private static Dictionary<string, byte[]> World(params (string Path, byte[] Bytes)[] files)
    { var result = files.ToDictionary(p => p.Path, p => p.Bytes); result["level.dat"] = [1]; return result; }
    // Independent fixture encoder: raw NBT and Anvil sectors, with no production writer calls.
    private static byte[] Nbt(int x, int z, int value)
    {
        using var stream = new MemoryStream(); stream.Write([10, 0, 0]);
        foreach (var pair in new[] { ("DataVersion", 3700), ("xPos", x), ("zPos", z), ("value", value) })
        {
            var name = Encoding.UTF8.GetBytes(pair.Item1); stream.WriteByte(3); stream.WriteByte(0); stream.WriteByte((byte)name.Length); stream.Write(name);
            var number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, pair.Item2); stream.Write(number);
        }
        stream.WriteByte(0); return stream.ToArray();
    }
    private static byte[] TerrainNbt(int x, int z, int value, string shape)
    {
        using var stream = new MemoryStream(); stream.Write(Nbt(x, z, value).AsSpan()[..^1]);
        void Tag(byte type, string name, byte[] payload)
        {
            var text = Encoding.UTF8.GetBytes(name);
            stream.WriteByte(type); stream.WriteByte(0); stream.WriteByte((byte)text.Length);
            stream.Write(text); stream.Write(payload);
        }
        var status = Encoding.UTF8.GetBytes("minecraft:biomes");
        Tag(8, "Status", [0, (byte)status.Length, .. status]);
        if (shape is "nested" or "legacy") Tag(10, shape == "legacy" ? "Level" : "unknown", []);
        if (shape == "mixed") Tag(9, "Entities", [10, 0, 0, 0, 1, 0]);
        if (shape == "scalar") Tag(1, "entities", [0]);
        else Tag(9, shape == "empty_upper" ? "Entities" : "entities", shape switch
        {
            "empty_end" => [0, 0, 0, 0, 0], "populated" => [10, 0, 0, 0, 1, 0],
            "wrong_list" => [3, 0, 0, 0, 0], _ => [10, 0, 0, 0, 0]
        });
        if (shape is "nested" or "legacy") stream.WriteByte(0);
        stream.WriteByte(0); return stream.ToArray();
    }
    private static byte[] Region(params (int Slot, byte[] Payload)[] chunks)
    {
        var bytes = new byte[8192 + chunks.Length * 4096]; var sector = 2;
        foreach (var (slot, payload) in chunks.OrderBy(c => c.Slot))
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(slot * 4), sector << 8 | 1);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4096 + slot * 4), (uint)(100 + slot));
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(sector * 4096), payload.Length + 1);
            bytes[sector * 4096 + 4] = 3; payload.CopyTo(bytes, sector * 4096 + 5); sector++;
        }
        return bytes;
    }
    private static byte[] ExternalRegion()
    { var bytes = Region((0, Array.Empty<byte>())); bytes[8196] = 131; bytes[^1] = 123; return bytes; }
    private static byte[] Unpadded(byte[] bytes)
    {
        var end = 8192;
        for (var slot = 0; slot < 1024; slot++)
        {
            var location = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(slot * 4));
            if (location == 0) continue;
            var offset = (int)(location >> 8) * 4096;
            end = Math.Max(end, offset + 4 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)));
        }
        return bytes[..end];
    }
    private static async Task<Run> Analyze(Dictionary<string, byte[]> b, Dictionary<string, byte[]> o, Dictionary<string, byte[]> t,
        string policy = "conservative", MergeInputBoundary? boundary = null, Func<MergeCoverageQuery, MergeCoverageState>? coverage = null)
    {
        var plugin = new MinecraftSavesPlugin(); await plugin.ActivateAsync(new Activation(), default);
        var views = new[] { new View(b, boundary?.Fingerprint ?? "all", coverage), new View(o, boundary?.Fingerprint ?? "all", coverage), new View(t, boundary?.Fingerprint ?? "all", coverage) };
        MergeVersionInput Input(int i) => new("version-" + i, "representation-" + i, "tree-" + i,
            boundary ?? new(1, MergeBoundaryScope.All, [], MergeBoundaryFilter.Blacklist, [], false, "all"), views[i]);
        var request = new MergeAnalysisRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(new(MinecraftSavesPlugin.PluginIdentity), MinecraftSavesPlugin.MinecraftKindIdentity), policy, policy == "terrain-first" ? "4" : "3", Input(0), Input(1), Input(2), MergeLimits.Default);
        var plan = new Plan(); var context = new PluginInvocationContext(new(MinecraftSavesPlugin.PluginIdentity), null!, default, default);
        var result = await ((IMergeProviderCapability)plugin).AnalyzeAsync(request, plan, context);
        return new((IMergeProviderCapability)plugin, request, plan, views, context) { AnalysisResult = result };
    }
    private sealed record Run(IMergeProviderCapability Provider, MergeAnalysisRequest Request, Plan Plan, View[] Views, PluginInvocationContext Context)
    {
        internal MergeProviderResult AnalysisResult { get; init; } = null!;
        internal async Task<Dictionary<string, byte[]>> Build(MergeDecisionKind choice = MergeDecisionKind.Ours, bool overrideAutomatic = false)
        {
            var files = new Dictionary<string, byte[]>();
            foreach (var group in Plan.Groups)
                await Provider.BuildAsync(new(Request, group, new Decisions(Plan, group.GroupId, choice, overrideAutomatic)), new Output(files, Views), Context);
            return files;
        }
    }
    private sealed class Activation : IPluginActivationContext
    {
        public PluginId PluginId => new(MinecraftSavesPlugin.PluginIdentity);
        public PluginSettingsSnapshot Settings => new(PluginId, new Dictionary<string, JsonElement>());
        public IReadOnlyList<ConfigSnapshot> Configs => [];
        public void RegisterCapability<T>(T capability) where T : class, IPluginCapability { }
    }
    private sealed class View(Dictionary<string, byte[]> files, string fingerprint, Func<MergeCoverageQuery, MergeCoverageState>? coverage) : IMergeSourceView
    {
        public Dictionary<string, byte[]> Files => files;
        public ValueTask<MergeCoverageResult> CheckCoverageAsync(MergeCoverageQuery query, CancellationToken token)
            => ValueTask.FromResult(new MergeCoverageResult(query.RequirementId, fingerprint, coverage?.Invoke(query) ?? MergeCoverageState.Complete,
                [new("test-proof", "historical-filter", null)]));
        public ValueTask<MergeInputPage> ReadPageAsync(string? cursor, int count, CancellationToken token)
        {
            var start = int.Parse(cursor ?? "0"); var entries = files.Skip(start).Take(count)
                .Select(p => new MergeInputEntry(p.Key, false, p.Value.Length, Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray();
            return ValueTask.FromResult(new MergeInputPage(entries, start + entries.Length < files.Count ? (start + entries.Length).ToString() : null));
        }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => ValueTask.FromResult<Stream>(new MemoryStream(files[path], false));
    }
    private sealed class Capture(Action<byte[]> done) : MemoryStream
    { protected override void Dispose(bool disposing) { if (disposing) done(ToArray()); base.Dispose(disposing); } }
    private sealed class Plan : IMergePlanWriter
    {
        public List<MergeOutputGroup> Groups { get; } = [];
        public List<MergeSubject> Subjects { get; } = [];
        public Dictionary<string, byte[]> Data { get; } = [];
        public ValueTask AddGroupAsync(MergeOutputGroup group, CancellationToken token) { Groups.Add(group); return ValueTask.CompletedTask; }
        public ValueTask ClaimPathsAsync(string group, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask AddSubjectsAsync(IReadOnlyList<MergeSubject> subjects, CancellationToken token) { Subjects.AddRange(subjects); return ValueTask.CompletedTask; }
        public ValueTask<Stream> OpenDataWriteAsync(string key, CancellationToken token) => ValueTask.FromResult<Stream>(new Capture(bytes => Data[key] = bytes));
        public ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken token) => ValueTask.FromResult<Stream>(new MemoryStream(Data[key], false));
    }
    private sealed class Decisions(Plan plan, string group, MergeDecisionKind choice, bool overrideAutomatic = false) : IMergeDecisionReader
    {
        public ValueTask<MergeDecisionPage> ReadPageAsync(string? cursor, int count, CancellationToken token)
        {
            var start = int.Parse(cursor ?? "0"); var all = plan.Subjects.Where(s => s.GroupId == group).ToArray();
            var rows = all.Skip(start).Take(count).Select(s => new MergeSubjectDecision(s, s.IsAutomatic && !(overrideAutomatic && s.CanOverrideAutomatic) ? MergeDecisionKind.Automatic : choice, null)).ToArray();
            return ValueTask.FromResult(new MergeDecisionPage(rows, start + rows.Length < all.Length ? (start + rows.Length).ToString() : null));
        }
        public ValueTask<Stream> OpenDataReadAsync(string key, CancellationToken token) => plan.OpenDataReadAsync(key, token);
        public ValueTask<Stream> OpenManualReadAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Output(Dictionary<string, byte[]> files, View[] views) : IMergeOutputWorkspace
    {
        public ValueTask ReuseAsync(string path, MergeInputSide side, string source, CancellationToken token) { files.Add(path, views[(int)side].Files[source]); return ValueTask.CompletedTask; }
        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken token) => ValueTask.FromResult<Stream>(new Capture(bytes => files.Add(path, bytes)));
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token) => ValueTask.FromResult<Stream>(new MemoryStream(files[path], false));
        public ValueTask DeleteAsync(string path, CancellationToken token) { files.Remove(path); return ValueTask.CompletedTask; }
    }
}
