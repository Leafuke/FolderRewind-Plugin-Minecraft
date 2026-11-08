using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SelectedScopeIgnoresUnselectedLinkedComponentsButRejectsSelectedLinks(bool map)
    {
        using var world = TemporaryWorld.Create(); using var links = new SnapshotTestLinks(world.Root);
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "region"));
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "DIM-1"));
        var outside = Path.Combine(world.Root, "outside"); Directory.CreateDirectory(outside);
        links.Junctions([(Path.Combine(world.WorldPath, "DIM-1", "entities"), outside)]);
        var rules = await SelectedScopePatterns(world.WorldPath, map);
        CollectionAssert.Contains(rules.ToArray(), "region/r.0.0.mca");
        Assert.IsFalse(rules.Any(rule => rule.StartsWith("DIM-1/", StringComparison.Ordinal)));
        if (map) await Assert.ThrowsExactlyAsync<IOException>(() => SelectedScopePatterns(world.WorldPath, true, MinecraftPathGrammar.Nether));
        else
        {
            Assert.IsFalse(MinecraftRegionBackupScope.TryBuild(world.WorldPath, world.WorldPath,
                new Dictionary<string, string> { ["regions"] = "0,0", ["dimensions"] = "nether" }, out _, out var error));
            Assert.AreEqual("dimension_layout_unavailable", error);
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SelectedScopeDoesNotEnumerateUnselectedUnreadableOrDeepDimensions(bool map)
    {
        using var world = TemporaryWorld.Create();
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "region"));
        var unreadable = new DirectoryInfo(Directory.CreateDirectory(Path.Combine(world.WorldPath, "DIM-1", "entities")).FullName);
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "dimensions", "mod", string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("deep", 20))));
        using var identity = WindowsIdentity.GetCurrent();
        var original = unreadable.GetAccessControl(); var denied = unreadable.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        unreadable.SetAccessControl(denied);
        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(unreadable.FullName));
            var rules = await SelectedScopePatterns(world.WorldPath, map);
            CollectionAssert.Contains(rules.ToArray(), "region/r.0.0.mca");
        }
        finally { unreadable.SetAccessControl(original); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task SelectedScopeKeepsExplicitSourceAliasesAndCancellation(bool map)
    {
        using var world = TemporaryWorld.Create(); using var links = new SnapshotTestLinks(world.Root);
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "region"));
        var alias = Path.Combine(world.Root, "selected-root"); links.Junctions([(alias, world.WorldPath)]);
        CollectionAssert.Contains((await SelectedScopePatterns(alias, map)).ToArray(), "region/r.0.0.mca");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => SelectedScopePatterns(alias, map, token: new(true)));
    }

    [TestMethod]
    public async Task MapScopeResolvesAllSelectedCustomAndNestedWorldRoots()
    {
        using var world = TemporaryWorld.Create();
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "dimensions", "mod", "deep", "moon", "region"));
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "other", "DIM1", "entities"));
        File.WriteAllBytes(Path.Combine(world.WorldPath, "other", "level.dat"), []);
        var fixture = Activate(); var (config, folder) = Snapshots(world.WorldPath);
        var result = await ((ISpatialScopeSelectionCapability)fixture.Plugin).ResolveSelectionAsync(new(new(config, folder, Guid.NewGuid()),
            [new(".", "mod:deep/moon", new(0, 0, 512, 512)), new("other", MinecraftPathGrammar.End, new(-512, -512, 0, 0))]), fixture.Invocation);
        CollectionAssert.Contains(result.IncludePatterns.ToArray(), "dimensions/mod/deep/moon/region/r.0.0.mca");
        CollectionAssert.Contains(result.IncludePatterns.ToArray(), "other/DIM1/entities/r.-1.-1.mca");
        CollectionAssert.Contains(result.IncludePatterns.ToArray(), "other/DIM1/region/c.*.*.mcc");
    }

    [TestMethod]
    public async Task MapScopeKeepsPaperSourceBoundaryAndAmbiguityChecks()
    {
        using var world = TemporaryWorld.Create();
        var server = Path.Combine(world.Root, "server"); var primary = Path.Combine(server, "world");
        Directory.CreateDirectory(Path.Combine(primary, "region"));
        Directory.CreateDirectory(Path.Combine(server, "world_nether", "DIM-1", "entities"));
        var fixture = Activate(); var (config, folder) = Snapshots(server);
        var capability = (ISpatialScopeSelectionCapability)fixture.Plugin;
        var result = await capability.ResolveSelectionAsync(new(new(config, folder, Guid.NewGuid()),
            [new("world", MinecraftPathGrammar.Nether, new(0, 0, 512, 512))]), fixture.Invocation);
        CollectionAssert.Contains(result.IncludePatterns.ToArray(), "world_nether/DIM-1/region/r.0.0.mca");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SelectedScopePatterns(primary, true, MinecraftPathGrammar.Nether));
        Directory.CreateDirectory(Path.Combine(primary, "DIM-1", "region"));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await capability.ResolveSelectionAsync(new(new(config, folder, Guid.NewGuid()),
            [new("world", MinecraftPathGrammar.Nether, new(0, 0, 512, 512))]), fixture.Invocation));
    }

    [TestMethod]
    [DataRow("mod:../escape")] [DataRow("mod:/root")] [DataRow("mod/name:value")] [DataRow("mod:value:extra")]
    [DataRow("mod:value\\escape")] [DataRow("missing-colon")]
    public async Task MapScopeRejectsDimensionIdentitiesThatAreNotSafePaths(string dimension)
    {
        using var world = TemporaryWorld.Create();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => SelectedScopePatterns(world.WorldPath, true, dimension));
    }

    private static async Task<IReadOnlyList<string>> SelectedScopePatterns(string source, bool map,
        string dimension = MinecraftPathGrammar.Overworld, CancellationToken token = default)
    {
        var fixture = Activate(); var (config, folder) = Snapshots(source);
        var invocation = fixture.Invocation with { OperationCancellation = token };
        var scope = new BackupScopeId(new OwnerId("com.folderrewind.minerewind"), "selected-regions");
        var parameters = new Dictionary<string, JsonElement>();
        if (map)
        {
            var result = await ((ISpatialScopeSelectionCapability)fixture.Plugin).ResolveSelectionAsync(new(new(config, folder, Guid.NewGuid()),
                [new(".", dimension, new(0, 0, 512, 512))]), invocation);
            foreach (var pair in result.Parameters) parameters.Add(pair.Key, JsonSerializer.SerializeToElement(pair.Value));
        }
        else
        {
            parameters.Add("regions", JsonSerializer.SerializeToElement("0,0"));
            parameters.Add("dimensions", JsonSerializer.SerializeToElement(dimension));
        }
        var resolved = await fixture.Plugin.ResolveAsync(new BackupScopeRequest(config, folder, scope, parameters), invocation);
        Assert.AreEqual(OperationReadiness.Ready, resolved.Readiness, JsonSerializer.Serialize(resolved.Diagnostics));
        return resolved.IncludePatterns;
    }
}
