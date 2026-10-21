using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public void LockProbeDistinguishesSharingFailuresFromAccessAndDeviceErrors()
    {
        using var world = TemporaryWorld.Create();
        Assert.AreEqual(WorldLockState.Released, MinecraftWorldLockProbe.Inspect(world.WorldPath).State);
        Assert.AreEqual(WorldLockState.Unknown, MinecraftWorldLockProbe.Inspect(Path.Combine(world.WorldPath, "vanished")).State);
        foreach (var code in new[] { 32, 33, 5, 1117 })
        {
            var probe = MinecraftWorldLockProbe.Inspect(world.WorldPath,
                _ => throw new IOException("injected filesystem failure", unchecked((int)0x80070000) | code));
            Assert.AreEqual(code is 32 or 33 ? WorldLockState.InUse : WorldLockState.Unknown, probe.State);
            Assert.AreEqual($"0x{(unchecked((int)0x80070000) | code):X8}", probe.HResult);
        }
        using var held = world.AcquireSessionLock();
        Assert.AreEqual(WorldLockState.InUse, MinecraftWorldLockProbe.Inspect(world.WorldPath).State);
    }

    [TestMethod]
    public async Task UnknownWorldStateWarnsPreferredBackupAndBlocksRequiredBackupAndRestore()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        fixture.Plugin.ProbeWorldLock = path => MinecraftWorldLockProbe.Inspect(path, _ => throw new UnauthorizedAccessException("access evidence"));
        var (config, folder) = Snapshots(world.WorldPath);
        await using var preferred = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Prefer), fixture.Invocation);
        var warning = preferred.Diagnostics.Single();
        Assert.AreEqual("minerewind.world_lock_probe_failed", warning.Code);
        Assert.AreEqual(DiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Arguments["exceptionType"], "UnauthorizedAccessException");
        var failed = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation));
        StringAssert.Contains(failed.Message, warning.Code);
        Assert.IsInstanceOfType<UnauthorizedAccessException>(failed.InnerException);
        var calls = 0;
        var result = await fixture.Plugin.CoordinateAsync(new(config, [folder], "version", Guid.NewGuid(), WorkspaceOperationKind.Restore,
            _ => { calls++; return ValueTask.FromResult(OperationOutcome.Success); }), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome);
        Assert.AreEqual(warning.Code, result.Diagnostics.Single().Code);
        Assert.AreEqual(0, calls);
        Assert.IsEmpty(fixture.Services.KnotLink.Events);
        fixture.Services.Configs.QueryResults.Add(config);
        var command = await fixture.Plugin.ExecuteAsync(new(new PluginCommandId(PluginId, "hotbackup.active-world"), new Dictionary<string, System.Text.Json.JsonElement>()), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, command.Outcome);
        Assert.AreEqual(warning.Code, command.Diagnostics.Single().Code);
        Assert.IsEmpty(fixture.Services.Backups.Requests);
    }

    [TestMethod]
    public async Task WorldStateIsCheckedAgainImmediatelyBeforeTheContinuation()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var inspections = 0;
        fixture.Plugin.ProbeWorldLock = path => new(++inspections == 1 ? WorldLockState.Released : WorldLockState.Unknown,
            Path.Combine(path, "session.lock"), new IOException("device disappeared"));
        var (config, folder) = Snapshots(world.WorldPath);
        var calls = 0;
        var result = await fixture.Plugin.CoordinateAsync(new(config, [folder], "version", Guid.NewGuid(), WorkspaceOperationKind.Restore,
            _ => { calls++; return ValueTask.FromResult(OperationOutcome.Success); }), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome);
        Assert.AreEqual(0, calls);
        Assert.AreEqual("minerewind.world_lock_probe_failed", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task ProbeFailureWhileWaitingReleasesTheSignalGateForTheNextRestore()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        fixture.Services.KnotLink.OnSendAsync = async (name, _, _) =>
        {
            if (name == "handshake") await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE", new Dictionary<string, string> { ["mod_version"] = "3.0.0" }, fixture.Invocation);
            if (name == "pre_hot_restore") await fixture.Plugin.ExecuteAsync("WORLD_SAVE_AND_EXIT_COMPLETE", new Dictionary<string, string>(), fixture.Invocation);
            if (name == "rejoin_world") await fixture.Plugin.ExecuteAsync("REJOIN_RESULT", new Dictionary<string, string> { ["result"] = "success" }, fixture.Invocation);
        };
        var inspections = 0;
        fixture.Plugin.ProbeWorldLock = path => new(++inspections == 1 ? WorldLockState.InUse : WorldLockState.Unknown,
            Path.Combine(path, "session.lock"), new IOException("device disconnected"));
        var calls = 0;
        var request = new RestoreCoordinatorRequest(config, [folder], "version", Guid.NewGuid(), WorkspaceOperationKind.Restore,
            _ => { calls++; return ValueTask.FromResult(OperationOutcome.Success); });
        var first = await fixture.Plugin.CoordinateAsync(request, fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, first.Outcome);
        Assert.AreEqual("minerewind.world_lock_probe_failed", first.Diagnostics.Single().Code);
        Assert.AreEqual(0, calls);
        inspections = 0;
        fixture.Plugin.ProbeWorldLock = path => new(++inspections == 1 ? WorldLockState.InUse : WorldLockState.Released, Path.Combine(path, "session.lock"));
        var second = await fixture.Plugin.CoordinateAsync(request with { OperationId = Guid.NewGuid() }, fixture.Invocation).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(OperationOutcome.Success, second.Outcome);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ReadonlyUnlockedSessionFileDoesNotRequireHotCoordination()
    {
        using var world = TemporaryWorld.Create();
        var path = Path.Combine(world.WorldPath, "session.lock");
        File.WriteAllBytes(path, [0]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var fixture = Activate();
            fixture.Services.KnotLink.IsAvailable = false;
            var (config, folder) = Snapshots(world.WorldPath);
            await using var backup = await fixture.Plugin.AcquireAsync(
                new BackupConsistencyRequest(config, folder, ConsistencyIntent.Require), fixture.Invocation);
            Assert.IsEmpty(backup.Diagnostics);
            var calls = 0;
            var restored = await fixture.Plugin.CoordinateAsync(new(config, [folder], "version", Guid.NewGuid(),
                WorkspaceOperationKind.Restore, _ => { calls++; return ValueTask.FromResult(OperationOutcome.Success); }), fixture.Invocation);
            Assert.AreEqual(OperationOutcome.Success, restored.Outcome);
            Assert.AreEqual(1, calls);
            Assert.IsEmpty(fixture.Services.KnotLink.Events);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}
