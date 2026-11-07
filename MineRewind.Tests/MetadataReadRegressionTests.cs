using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task RegionScanFailureRetainsFieldsAndExplicitlyReportsLowerBound()
    {
        using var world = TemporaryWorld.Create(); WriteLegacyLevelDat(world.WorldPath, "Retained", 0, 42, 1);
        for (var i = 0; i < 6; i++) Directory.CreateDirectory(Path.Combine(world.WorldPath, "empty" + i));
        var (config, folder) = Snapshots(world.WorldPath); var fixture = Activate();
        var result = await MinecraftMetadataService.ReadAsync(new(config, folder), fixture.Invocation, new WorldScanBudget(3));
        Assert.AreEqual("Retained", result.Fields.Single(f => f.Key == "worldName").Value.Default);
        Assert.Contains("At least", result.Fields.Single(f => f.Key == "regionFileCount").Value.Default);
        Assert.Contains("至少", result.Fields.Single(f => f.Key == "regionFileCount").Value.Translations["zh-CN"]);
        Assert.IsNotEmpty(result.Diagnostics);
    }
    [TestMethod]
    public async Task MultiWorldMetadataDoesNotBorrowSingleWorldFieldsAndCancellationPropagates()
    {
        using var world = TemporaryWorld.Create(); var second = Path.Combine(Path.GetDirectoryName(world.WorldPath)!, "Second");
        Directory.CreateDirectory(second); WriteLegacyLevelDat(world.WorldPath, "First", 0, 1, 1); WriteLegacyLevelDat(second, "Second", 1, 2, 2);
        var (config, folder) = Snapshots(Path.GetDirectoryName(world.WorldPath)!); var fixture = Activate();
        var result = await fixture.Plugin.ReadAsync(new(config, folder), fixture.Invocation);
        Assert.AreEqual("2", result.Fields.Single(f => f.Key == "worldCount").Value.Default);
        Assert.IsFalse(result.Fields.Any(f => f.Key is "seed" or "worldName" or "gameMode"));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => fixture.Plugin.ReadAsync(new(config, folder),
            fixture.Invocation with { OperationCancellation = new(true) }).AsTask());
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LockedOrOversizedLevelDataProducesLocalizedNoticeAndKeepsRegionCount(bool locked)
    {
        using var world = TemporaryWorld.Create(); var fixture = Activate(); var (config, folder) = Snapshots(world.WorldPath);
        using var stream = new FileStream(Path.Combine(world.WorldPath, "level.dat"), FileMode.Open, FileAccess.ReadWrite, locked ? FileShare.None : FileShare.ReadWrite);
        if (!locked) { stream.SetLength(16 * 1024 * 1024 + 1); stream.Flush(); }
        var result = await fixture.Plugin.ReadAsync(new(config, folder), fixture.Invocation);
        Assert.IsTrue(result.Fields.Any(f => f.Key == "regionFileCount"));
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "minerewind.metadata_level_dat_invalid" && d.Arguments.ContainsKey("zh-CN")));
    }
    [TestMethod]
    public async Task DisplaySkipsLinkedSubtreesButFixedPreviewRejectsThem()
    {
        using var world = TemporaryWorld.Create(); using var links = new SnapshotTestLinks(world.Root);
        WriteLegacyLevelDat(world.WorldPath, "Linked", 0, 1, 1);
        var outside = Path.Combine(world.Root, "outside"); Directory.CreateDirectory(outside);
        links.Junctions([(Path.Combine(world.WorldPath, "linked"), outside)]);
        var fixture = Activate(); var (config, folder) = Snapshots(world.WorldPath);
        var result = await fixture.Plugin.ReadAsync(new(config, folder), fixture.Invocation);
        Assert.IsNotEmpty(result.Diagnostics);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession();
        Assert.IsTrue(session.Describe(source, default).Diagnostics.Any(d => d.Code == "preview.incomplete"));
        Assert.ThrowsExactly<IOException>(() => session.Describe(source with { FixedInput = new("fixed", world.WorldPath, "digest") }, default));
        session.Clear();
    }
    [TestMethod]
    [DataRow("dimensions")]
    [DataRow("region")]
    public void DisplaySkipsLinkedWorldDirectoriesAndKeepsReadableDimensions(string linkedDirectory)
    {
        using var world = TemporaryWorld.Create(); using var links = new SnapshotTestLinks(world.Root);
        var nether = Path.Combine(world.WorldPath, "DIM-1", "region", "r.0.0.mca");
        WritePreviewRegion(nether, 0);
        var expectedRegions = new List<string> { nether };
        if (linkedDirectory != "region")
        {
            var overworld = Path.Combine(world.WorldPath, "region", "r.0.0.mca");
            WritePreviewRegion(overworld, 0); expectedRegions.Add(overworld);
        }
        var outside = Path.Combine(world.Root, "outside");
        WritePreviewRegion(Path.Combine(outside, "r.9.9.mca"), 0);
        WritePreviewRegion(Path.Combine(outside, "foreign", "dimension", "region", "r.9.9.mca"), 0);
        links.Junctions([(Path.Combine(world.WorldPath, linkedDirectory), outside)]);

        var scanned = WorldScanner.Scan(world.WorldPath, world.WorldPath, rejectLinks: false);
        Assert.IsTrue(scanned.IsIncomplete);
        CollectionAssert.AreEquivalent(expectedRegions, scanned.Dimensions.SelectMany(d => d.Regions.Values).ToArray());
        Assert.ThrowsExactly<IOException>(() => WorldScanner.Scan(world.WorldPath, world.WorldPath));
        var (config, folder) = Snapshots(world.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession();
        try
        {
            var description = session.Describe(source, default);
            Assert.IsTrue(description.Layers.Any(l => l.Id == "minecraft:the_nether"));
            Assert.IsTrue(description.Diagnostics.Any(d => d.Code == "preview.incomplete"));
            Assert.ThrowsExactly<IOException>(() => session.Describe(source with { FixedInput = new("fixed", world.WorldPath, "digest") }, default));
        }
        finally { session.Clear(); }
    }

    [TestMethod]
    public void DiscoveryDoesNotReenterASkippedLinkedLauncherConfiguration()
    {
        using var selected = TemporaryWorld.Create(); using var outside = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(selected.Root);
        Directory.CreateDirectory(Path.Combine(outside.Root, "config"));
        File.WriteAllText(Path.Combine(outside.Root, "config", "game-directories.json"),
            System.Text.Json.JsonSerializer.Serialize(new { directories = new[] { new { path = Path.Combine(outside.Root, ".minecraft"), name = "Outside" } } }));
        links.Junctions([(Path.Combine(selected.Root, ".hmcl"), outside.Root)]);
        var environment = new Discovery.MinecraftDiscoveryEnvironment { Folder = _ => "", Variable = _ => null, RegistryValue = (_, _) => null };
        var result = Discovery.MinecraftDiscoveryService.Discover(new([selected.Root]), default, environment);
        Assert.AreEqual(selected.WorldPath, result.Instances.Single().Worlds.Single().Path);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "minerewind.discovery_link_skipped"));
    }
    [TestMethod]
    public void DiscoveryDoesNotUseLinkedMetadataAsAWorldMarker()
    {
        using var selected = TemporaryWorld.Create(); using var outside = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(selected.Root);
        var metadata = Path.Combine(selected.WorldPath, "level.dat"); File.Delete(metadata);
        links.Symbolic(metadata, Path.Combine(outside.WorldPath, "level.dat"), directory: false);
        var environment = new Discovery.MinecraftDiscoveryEnvironment { Folder = _ => "", Variable = _ => null, RegistryValue = (_, _) => null };
        var result = Discovery.MinecraftDiscoveryService.Discover(new([selected.WorldPath]), default, environment);
        Assert.IsEmpty(result.Instances);
        Assert.IsTrue(result.Diagnostics.Any(d => d.Code == "minerewind.discovery_link_skipped"));
    }
}
