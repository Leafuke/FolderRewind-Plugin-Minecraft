using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind.Tests;

public sealed partial class SemanticMergeTests
{
    [TestMethod]
    public async Task RepeatedRegionMetadataStillVerifiesEverySideAndKeepsExactIndices()
    {
        var world = RuntimeWorld("minecraft:stone", 1);
        var cache = new MinecraftMergeAnalysisCache();
        var first = await ReadCached(cache, world);
        var second = await ReadCached(cache, world);
        Assert.AreEqual(1, cache.RegionCount); Assert.AreEqual(1, cache.FingerprintCount);
        Assert.AreEqual(1, first.View.OpenCounts["region/r.0.0.mca"]);
        Assert.AreEqual(1, second.View.OpenCounts["region/r.0.0.mca"]);
        Assert.AreEqual(JsonSerializer.Serialize(first.Index), JsonSerializer.Serialize(second.Index));
        var reference = await MergeRegion.ReadAsync("region/r.0.0.mca", first.Index.Address, first.View.OpenReadAsync,
            world.Keys.ToHashSet(StringComparer.Ordinal), default, classifyContent: true, allowEmptyRegion: true);
        Assert.AreEqual(JsonSerializer.Serialize(reference), JsonSerializer.Serialize(second.Index));
    }

    [TestMethod]
    public async Task ExternalOnlyChangesAndCompleteExternalFamilyPreventRegionReuse()
    {
        var a = World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 1)));
        var b = World(("region/r.0.0.mca", ExternalRegion()), ("region/c.0.0.mcc", Nbt(0, 0, 2)));
        var cache = new MinecraftMergeAnalysisCache();
        var first = await ReadCached(cache, a); var changed = await ReadCached(cache, b); var equal = await ReadCached(cache, a);
        Assert.AreEqual(2, cache.RegionCount);
        Assert.AreNotEqual(first.Index.Chunks.Single().PayloadHash, changed.Index.Chunks.Single().PayloadHash);
        Assert.AreEqual(first.Index.Chunks.Single().PayloadHash, equal.Index.Chunks.Single().PayloadHash);
        Assert.AreEqual(1, equal.View.OpenCounts["region/c.0.0.mcc"], "A reused index still opens its external payload through this side's verifier.");
        a["region/c.1.0.mcc"] = [1];
        var extra = await ReadCached(cache, a);
        Assert.AreEqual(3, cache.RegionCount, "An unreferenced family member is part of the cache identity.");
        Assert.IsFalse(extra.View.OpenCounts.ContainsKey("region/c.1.0.mcc"), "Only originally referenced files require opening on a hit/read.");
        a.Remove("region/c.0.0.mcc");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadCached(cache, a));
        Assert.AreEqual(3, cache.RegionCount, "Failed region reads never enter the cache.");
    }

    [TestMethod]
    public async Task BoundaryChangesAndUnknownDigestsCannotReuseRegionMetadata()
    {
        var world = RuntimeWorld("minecraft:stone", 1); var cache = new MinecraftMergeAnalysisCache();
        await ReadCached(cache, world, boundary: "one"); await ReadCached(cache, world, boundary: "two");
        Assert.AreEqual(2, cache.RegionCount);
        await ReadCached(cache, world, boundary: "two", knownDigest: false);
        Assert.AreEqual(2, cache.RegionCount);
        var failed = World(("region/r.0.0.mca", [1]));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ReadCached(cache, failed));
        Assert.AreEqual(2, cache.RegionCount);
    }

    [TestMethod]
    public async Task CachedMetadataPropagatesVerificationFailureAndCancellation()
    {
        var world = RuntimeWorld("minecraft:stone", 1); var cache = new MinecraftMergeAnalysisCache();
        await ReadCached(cache, world);
        await Assert.ThrowsExactlyAsync<IOException>(() => ReadCached(cache, world, onOpen: _ => throw new IOException("fixed input changed")));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ReadCached(cache, world,
            onOpen: _ => cancellation.Cancel(), token: cancellation.Token));
        Assert.AreEqual(1, cache.RegionCount);
    }

    [TestMethod]
    public async Task UnsupportedFingerprintIsCachedWithoutChangingRawIdentityFallback()
    {
        var world = RuntimeWorld("minecraft:stone", 1, root => root["DataVersion"] = new fNbt.NbtInt("DataVersion", int.MaxValue));
        var cache = new MinecraftMergeAnalysisCache(); var first = await ReadCached(cache, world);
        var differentContainer = world.ToDictionary(p => p.Key, p => p.Value.ToArray());
        differentContainer["region/r.0.0.mca"][4096] ^= 1; // Timestamp only, same valid NBT payload.
        var second = await ReadCached(cache, differentContainer);
        Assert.AreEqual(2, cache.RegionCount); Assert.AreEqual(1, cache.FingerprintCount);
        Assert.IsNull(first.Index.Chunks.Single().ContentIdentity); Assert.IsNull(second.Index.Chunks.Single().ContentIdentity);
        Assert.AreEqual(first.Index.Chunks.Single().PayloadHash, second.Index.Chunks.Single().PayloadHash);
    }

    [TestMethod]
    [DataRow("conservative")]
    [DataRow("terrain-first")]
    public async Task EqualBaseAndTargetStillKeepTargetWhenOnlySourceRuntimeChanges(string policy)
    {
        var target = RuntimeWorld("minecraft:stone", 1); var source = RuntimeWorld("minecraft:stone", 2);
        var run = await Analyze(target, target, source, policy);
        Assert.AreEqual(1, run.Plan.Subjects.Single().State.GetProperty("AutomaticSide").GetInt32());
        CollectionAssert.AreEqual(target["region/r.0.0.mca"], (await run.Build())["region/r.0.0.mca"]);
    }

    [TestMethod]
    public async Task EqualEmbeddedInputsRetainWholeWorldAndTerrainFirstPinningDecisions()
    {
        var world = World(("region/r.0.0.mca", Region((0, TerrainNbt(0, 0, 1, "populated")))));
        var conservative = await Analyze(world, world, world);
        Assert.IsTrue(conservative.Plan.Groups.Single().State.GetProperty("WholeWorld").GetBoolean());
        var terrain = await Analyze(world, world, world, "terrain-first");
        Assert.IsFalse(terrain.Plan.Groups.Single().State.GetProperty("WholeWorld").GetBoolean());
        Assert.IsFalse(terrain.Plan.Subjects.Single().CanOverrideAutomatic);
        Assert.AreEqual("minecraft.merge.embedded_terrain_kept_ours", terrain.AnalysisResult.Diagnostics.Single().Code);
        CollectionAssert.AreEqual(world["region/r.0.0.mca"], (await terrain.Build())["region/r.0.0.mca"]);
    }

    [TestMethod]
    public async Task AnalysisMetadataCachesRespectTheirCoordinateBudgets()
    {
        var world = RuntimeWorld("minecraft:stone", 1); var regions = new MinecraftMergeAnalysisCache();
        for (var i = 0; i < MinecraftMergeAnalysisCache.MaximumRegions + 2; i++) await ReadCached(regions, world, boundary: "boundary-" + i);
        Assert.AreEqual(MinecraftMergeAnalysisCache.MaximumRegions, regions.RegionCount);
        var chunks = new MergeChunkFingerprintCache();
        for (var i = 0; i < MergeChunkFingerprintCache.MaximumEntries + 2; i++)
        {
            var bytes = Nbt(0, 0, i); // Structurally valid, unsupported content (no sections).
            Assert.IsNull(chunks.ReadValidated(bytes, bytes.Length, new(0, 0), Convert.ToHexString(SHA256.HashData(bytes)), default));
        }
        Assert.AreEqual(MergeChunkFingerprintCache.MaximumEntries, chunks.Count);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cached = Nbt(0, 0, 0);
        Assert.ThrowsExactly<OperationCanceledException>(() => chunks.ReadValidated(cached, cached.Length, new(0, 0),
            Convert.ToHexString(SHA256.HashData(cached)), cancellation.Token));
    }

    private static async Task<(MergeRegionIndex Index, View View)> ReadCached(MinecraftMergeAnalysisCache cache,
        Dictionary<string, byte[]> files, string boundary = "all", bool knownDigest = true, Action<string>? onOpen = null,
        CancellationToken token = default)
    {
        const string path = "region/r.0.0.mca";
        var view = new View(files, boundary, null) { OnOpen = onOpen };
        var inventory = files.ToDictionary(p => p.Key, p => new MergeInputEntry(p.Key, false, p.Value.Length,
            knownDigest ? Convert.ToHexString(SHA256.HashData(p.Value)) : ""), StringComparer.Ordinal);
        var input = new MergeVersionInput("version", "representation", "tree", new(1, MergeBoundaryScope.All, [], MergeBoundaryFilter.Blacklist, [], false, boundary), view);
        Assert.IsTrue(MergeRegion.TryAddress(path, [""], out var address));
        var families = MinecraftMergeAnalysisCache.IndexExternalFamilies(files.Keys);
        return (await cache.ReadAsync(path, address!, input, inventory, files.Keys.ToHashSet(StringComparer.Ordinal),
            families.GetValueOrDefault(("region/", 0, 0)) ?? [], token), view);
    }
}
