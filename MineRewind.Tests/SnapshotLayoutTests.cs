using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task SnapshotDistinguishesRuntimeFilesFromSameNamedDirectories()
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, "paper");
        WriteSnapshotFile(world, "nested/session.lock/ordinary.dat", "directory content");
        WriteSnapshotFile(world, "DistantHorizons.sqlite/ordinary.dat", "directory content");
        WriteSnapshotFile(world, "prefixDistantHorizons.sqlite", "ordinary file");
        WriteSnapshotFile(world, "voxy", "ordinary file");
        WriteSnapshotFile(world, "nested/voxy/cache.bin", "cache");
        WriteSnapshotFile(world, "other/session.lock", "lock");
        var fixture = Activate(); var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        foreach (var relative in new[] { "nested/session.lock/ordinary.dat", "DistantHorizons.sqlite/ordinary.dat", "prefixDistantHorizons.sqlite", "voxy" })
            Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "world", relative)), relative);
        Assert.IsFalse(Directory.Exists(Path.Combine(lease.SourcePath, "world", "nested", "voxy")));
        Assert.IsFalse(File.Exists(Path.Combine(lease.SourcePath, "world", "other", "session.lock")));
    }
    [TestMethod]
    [DataRow("direct")]
    [DataRow("saves")]
    [DataRow("default-server")]
    [DataRow("custom-server")]
    [DataRow("paper")]
    [DataRow("modern")]
    public async Task SnapshotPreservesConfiguredSourceLayout(string layout)
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, layout);
        WriteSnapshotFile(world, "session.lock", "lock");
        WriteSnapshotFile(world, "voxy/cache.bin", "cache");
        WriteSnapshotFile(world, "DistantHorizons.sqlite", "cache");
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        string snapshot;
        await using (var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation))
        {
            snapshot = lease.SourcePath;
            Assert.IsTrue(lease.IsStableSourceView);
            Assert.IsEmpty(lease.Diagnostics);
            var expected = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is not ("session.lock" or "cache.bin" or "DistantHorizons.sqlite"))
                .ToDictionary(path => Path.GetRelativePath(source, path), File.ReadAllText);
            var actual = Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(snapshot, path), File.ReadAllText);
            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
            foreach (var file in expected) Assert.AreEqual(file.Value, actual[file.Key], file.Key);
            if (source != world) Assert.IsFalse(File.Exists(Path.Combine(snapshot, "level.dat")));
        }
        Assert.IsFalse(Directory.Exists(snapshot));
        Assert.IsTrue(File.Exists(Path.Combine(world, "level.dat")));
    }

    [TestMethod]
    [DataRow("paper")]
    [DataRow("modern")]
    public async Task RegionRulesAddressFilesInActualServerSnapshot(string layout)
    {
        using var temporary = TemporaryWorld.Create();
        var (source, _) = CreateSnapshotLayout(temporary.Root, layout);
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        var scope = await fixture.Plugin.ResolveAsync(new BackupScopeRequest(config, folder, fixture.Plugin.Scopes[0].Id,
            new Dictionary<string, JsonElement>
            {
                ["areas"] = JsonSerializer.SerializeToElement("0,0,1,1"),
                ["dimension.nether"] = JsonSerializer.SerializeToElement(true),
                ["dimension.end"] = JsonSerializer.SerializeToElement(true)
            }), fixture.Invocation);
        Assert.AreEqual(OperationReadiness.Ready, scope.Readiness);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        var selected = scope.IncludePatterns.Where(rule => rule.EndsWith("r.0.0.mca", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(9, selected);
        foreach (var rule in selected)
        {
            Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, rule)), rule);
            Assert.AreEqual(File.ReadAllText(Path.Combine(source, rule)), File.ReadAllText(Path.Combine(lease.SourcePath, rule)));
        }
        Assert.IsNotEmpty(Directory.GetFiles(lease.SourcePath, "r.1.0.mca", SearchOption.AllDirectories));
        Assert.IsFalse(scope.IncludePatterns.Any(rule => rule.EndsWith("r.1.0.mca", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("direct")]
    [DataRow("saves")]
    [DataRow("default-server")]
    [DataRow("custom-server")]
    public async Task MetadataReadsConfiguredRelativePathFromStableSnapshot(string layout)
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, layout);
        WriteLegacyLevelDat(world, "Captured", 1, 42, 7);
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        WriteLegacyLevelDat(world, "Changed live world", 0, 99, 1);
        var stable = new MemoryMetadataSourceView(Directory.GetFiles(lease.SourcePath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(lease.SourcePath, path).Replace('\\', '/'), File.ReadAllBytes));
        var result = await fixture.Plugin.CaptureAsync(new(config, folder, stable), fixture.Invocation);
        Assert.IsEmpty(result.Diagnostics);
        Assert.AreEqual("Captured", result.Snapshots.Single().Payload.GetProperty("levelName").GetString());
        var missing = await fixture.Plugin.CaptureAsync(new(config, folder,
            new MemoryMetadataSourceView(new Dictionary<string, byte[]>())), fixture.Invocation);
        Assert.IsEmpty(missing.Snapshots);
        Assert.AreEqual("minerewind.version_metadata_read_failed", missing.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task ServerBackupCoordinatesResolvedWorldButSnapshotsServerRoot()
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, "custom-server");
        using var sessionLock = new FileStream(Path.Combine(world, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var fixture = Activate();
        fixture.Services.KnotLink.OnSendAsync = async (name, arguments, _) =>
        {
            if (name == "handshake")
            {
                Assert.AreEqual("custom", arguments["world"]);
                await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE", new Dictionary<string, string> { ["mod_version"] = "3.0.0" }, fixture.Invocation);
            }
            if (name == "pre_hot_backup")
            {
                Assert.AreEqual("custom", arguments["world"]);
                await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation);
            }
        };
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "custom", "level.dat")));
        Assert.IsEmpty(lease.Diagnostics);
    }

    [TestMethod]
    public async Task SnapshotStorageFailureFallsBackToConfiguredRootOnlyWhenPreferred()
    {
        using var temporary = TemporaryWorld.Create();
        var (source, _) = CreateSnapshotLayout(temporary.Root, "paper");
        var fixture = Activate(new FakeHostServices { TemporaryStorage = new SnapshotTestStorage(() => throw new IOException("storage unavailable")) });
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Prefer), fixture.Invocation);
        Assert.AreEqual(source, lease.SourcePath);
        Assert.IsFalse(lease.IsStableSourceView);
        Assert.AreEqual("minerewind.snapshot_fallback", lease.Diagnostics.Single().Code);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation));
    }

    private sealed class SnapshotTestStorage(Func<string> create) : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken) => ValueTask.FromResult(create());
    }

    private static (string Source, string World) CreateSnapshotLayout(string temporary, string layout)
    {
        var source = Path.Combine(temporary, "source");
        var world = layout switch
        {
            "direct" => source,
            "saves" => Path.Combine(source, "MyWorld"),
            "custom-server" => Path.Combine(source, "custom"),
            _ => Path.Combine(source, "world")
        };
        WriteSnapshotFile(world, "level.dat", "level");
        if (layout is not ("direct" or "saves"))
            WriteSnapshotFile(source, "server.properties", layout == "custom-server" ? "level-name=custom" : "level-name=world");
        var dimensions = layout switch
        {
            "paper" => new[] { world, Path.Combine(source, "world_nether", "DIM-1"), Path.Combine(source, "world_the_end", "DIM1") },
            "modern" => new[] { "overworld", "the_nether", "the_end" }.Select(name => Path.Combine(world, "dimensions", "minecraft", name)).ToArray(),
            _ => new[] { world }
        };
        foreach (var dimension in dimensions)
        foreach (var kind in new[] { "region", "entities", "poi" })
        {
            WriteSnapshotFile(dimension, kind + "/r.0.0.mca", dimension + kind);
            WriteSnapshotFile(dimension, kind + "/r.1.0.mca", "unselected");
        }
        return (source, world);
    }

    private static void WriteSnapshotFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
