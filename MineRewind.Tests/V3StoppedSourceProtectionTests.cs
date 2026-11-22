using fNbt;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task StoppedWorldLeaseBlocksReopenAcrossCaptureAndInPlaceApplication()
    {
        using var world = CreateStoppedWorld();
        var fixture = Activate();
        var provider = (IStoppedSourceProtectionCapability)fixture.Plugin;
        var request = StoppedRequest(world.WorldPath);
        var marker = Path.Combine(world.WorldPath, "session.lock");

        var capture = await provider.AcquireAsync(request, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Stopped, capture.Check.State);
        Assert.AreEqual("4671", capture.Check.Observations["minecraft.dataVersion"]);
        Assert.AreEqual("vanilla", capture.Check.Observations["minecraft.loader"]);
        await using (var lease = capture.Lease!)
        {
            Assert.ThrowsExactly<IOException>(() => AttemptGameOpen(marker));
            Assert.IsTrue(File.ReadAllBytes(Path.Combine(world.WorldPath, "level.dat")).Length > 0);
            Assert.AreEqual(StoppedSourceState.Stopped, (await lease.RevalidateAsync(CancellationToken.None)).State);
        }

        using (var resumed = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var reopened = await provider.AcquireAsync(request with { Operation = StoppedSourceOperation.Apply,
                ExpectedObservations = capture.Check.Observations }, fixture.Invocation);
            Assert.AreEqual(StoppedSourceState.InUse, reopened.Check.State);
            Assert.IsNull(reopened.Lease);
        }

        var apply = await provider.AcquireAsync(request with { Operation = StoppedSourceOperation.Apply,
            ExpectedObservations = capture.Check.Observations }, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Stopped, apply.Check.State);
        await using (var lease = apply.Lease!)
        {
            var level = Path.Combine(world.WorldPath, "level.dat");
            var replacement = Path.Combine(world.Root, "replacement.dat");
            File.Copy(level, replacement);
            Assert.AreEqual(StoppedSourceState.Stopped, (await lease.RevalidateAsync(CancellationToken.None)).State);
            File.Move(replacement, level, overwrite: true);
            File.WriteAllText(Path.Combine(world.WorldPath, "region", "additional.txt"), "new");
            Assert.ThrowsExactly<IOException>(() => File.Move(marker, marker + ".replacement"));
            Assert.ThrowsExactly<IOException>(() => AttemptGameOpen(marker));
            Assert.AreEqual(StoppedSourceState.Stopped, (await lease.RevalidateAsync(CancellationToken.None)).State);
        }
        AttemptGameOpen(marker);
        CollectionAssert.AreEqual(new byte[] { 0xe2, 0x98, 0x83 }, File.ReadAllBytes(marker));
        Assert.IsEmpty(fixture.Services.KnotLink.Events);
    }

    [TestMethod]
    public async Task StoppedWorldReplayReacquiresExclusiveMarkerEvenWithPartiallyWrittenTree()
    {
        using var world = CreateStoppedWorld();
        var fixture = Activate();
        var provider = (IStoppedSourceProtectionCapability)fixture.Plugin;
        var request = StoppedRequest(world.WorldPath) with { Operation = StoppedSourceOperation.Replay };
        var marker = Path.Combine(world.WorldPath, "session.lock");
        File.Delete(Path.Combine(world.WorldPath, "level.dat"));
        using (world.AcquireSessionLock())
        {
            var occupied = await provider.AcquireAsync(request, fixture.Invocation);
            Assert.AreEqual(StoppedSourceState.InUse, occupied.Check.State);
            Assert.IsNull(occupied.Lease);
        }
        var resumed = await provider.AcquireAsync(request, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Stopped, resumed.Check.State);
        await using (var lease = resumed.Lease!)
        {
            Assert.ThrowsExactly<IOException>(() => AttemptGameOpen(marker));
            Assert.AreEqual(StoppedSourceState.Stopped, (await lease.RevalidateAsync(CancellationToken.None)).State);
        }
        File.Delete(marker);
        var absent = await provider.AcquireAsync(request, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Unknown, absent.Check.State);
        Assert.IsNull(absent.Lease);
        Assert.IsFalse(File.Exists(marker));

        fixture.Plugin.OpenStoppedWorldLock = _ => throw new UnauthorizedAccessException("denied fixture access");
        var denied = await provider.AcquireAsync(request, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Unknown, denied.Check.State);
        Assert.IsNull(denied.Lease);
        StringAssert.Contains(denied.Check.Diagnostics.Single().Arguments["exceptionType"], "UnauthorizedAccessException");
        Assert.IsEmpty(fixture.Services.KnotLink.Events);
    }

    [TestMethod]
    public async Task StrictProtectionRejectsIncompleteScopeUnknownLayoutAndIncompatibleEnvironment()
    {
        using var world = CreateStoppedWorld(version: 4786);
        var fixture = Activate();
        var provider = (IStoppedSourceProtectionCapability)fixture.Plugin;
        var request = StoppedRequest(world.WorldPath);
        var partial = request with { Boundary = request.Boundary with { Paths = new StoppedBoundaryView("playerdata/test.dat") } };
        Directory.CreateDirectory(Path.Combine(world.WorldPath, "playerdata"));
        File.WriteAllBytes(Path.Combine(world.WorldPath, "playerdata", "test.dat"), [1]);
        await Block(partial, "minerewind.stopped_source_scope_incomplete");
        await Block(request with { Boundary = request.Boundary with
            { FilterRules = [.. request.Boundary.FilterRules, new("datapacks", PathFilterTarget.Directory)] } },
            "minerewind.stopped_source_scope_incomplete");
        await Block(request with { Operation = StoppedSourceOperation.Apply },
            "minerewind.stopped_source_environment_unknown");
        await Block(request with { Operation = StoppedSourceOperation.Apply,
            ExpectedObservations = new Dictionary<string, string> { ["minecraft.dataVersion"] = "4671" } },
            "minerewind.stopped_source_environment_mismatch");

        var namespaced = await provider.AcquireAsync(request, fixture.Invocation);
        Assert.AreEqual(StoppedSourceState.Stopped, namespaced.Check.State);
        Assert.AreEqual("namespaced", namespaced.Check.Observations["minecraft.layout"]);
        await namespaced.Lease!.DisposeAsync();

        Directory.CreateDirectory(Path.Combine(world.WorldPath, "region"));
        File.WriteAllBytes(Path.Combine(world.WorldPath, "region", "r.0.0.mca"), new byte[8192]);
        await Block(request, "minerewind.stopped_source_layout_unsupported");
        Assert.IsEmpty(fixture.Services.KnotLink.Events);

        async Task Block(StoppedSourceProtectionRequest candidate, string code)
        {
            var result = await provider.AcquireAsync(candidate, fixture.Invocation);
            Assert.AreEqual(StoppedSourceState.Unknown, result.Check.State);
            Assert.IsNull(result.Lease);
            Assert.AreEqual(code, result.Check.Diagnostics.Single().Code);
            AttemptGameOpen(Path.Combine(world.WorldPath, "session.lock"));
        }
    }

    [TestMethod]
    public async Task StrictCaptureReleasesItsMarkerWhenCanceledAfterAcquisition()
    {
        using var world = CreateStoppedWorld();
        var fixture = Activate();
        var provider = (IStoppedSourceProtectionCapability)fixture.Plugin;
        using var canceled = new CancellationTokenSource();
        fixture.Plugin.OpenStoppedWorldLock = path =>
        {
            var guard = MinecraftStoppedWorldLock.Open(path);
            canceled.Cancel();
            return guard;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.AcquireAsync(StoppedRequest(world.WorldPath),
                fixture.Invocation with { OperationCancellation = canceled.Token }));
        AttemptGameOpen(Path.Combine(world.WorldPath, "session.lock"));
        Assert.IsEmpty(fixture.Services.KnotLink.Events);
    }

    private static TemporaryWorld CreateStoppedWorld(int version = 4671)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("The strict lock provider uses Windows sharing semantics.");
        var world = TemporaryWorld.Create();
        var level = new NbtCompound("") { new NbtCompound("Data")
        {
            new NbtInt("DataVersion", version), new NbtByte("WasModded", 0),
            new NbtList("ServerBrands", [new NbtString(null, "vanilla")]),
            new NbtCompound("DataPacks") { new NbtList("Enabled", [new NbtString(null, "vanilla")]) }
        } };
        new NbtFile(level).SaveToFile(Path.Combine(world.WorldPath, "level.dat"), NbtCompression.GZip);
        var region = version < 4786 ? Path.Combine(world.WorldPath, "region")
            : Path.Combine(world.WorldPath, "dimensions", "minecraft", "overworld", "region");
        Directory.CreateDirectory(region);
        File.WriteAllBytes(Path.Combine(region, "r.0.0.mca"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(world.WorldPath, "session.lock"), [0xe2, 0x98, 0x83]);
        return world;
    }

    private static StoppedSourceProtectionRequest StoppedRequest(string root)
    {
        var (config, folder) = Snapshots(root);
        return new(Guid.NewGuid(), config, folder, root,
            new(new StoppedBoundaryView(), "fixture", true, true, false, MinecraftWorldFilePolicy.BackupExclusions),
            StoppedSourceOperation.Capture);
    }

    private static void AttemptGameOpen(string path)
    {
        using var attempt = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    private sealed class StoppedBoundaryView(string? omittedPath = null) : IBackupSourceBoundaryView
    {
        public bool ContainsFile(string relativePath) => relativePath != omittedPath && !MinecraftWorldFilePolicy.Excludes(relativePath, false);
        public bool MayIntersectDirectory(string relativePath) => !MinecraftWorldFilePolicy.Excludes(relativePath, true);
    }
}
