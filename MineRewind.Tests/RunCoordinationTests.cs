using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    private sealed class WholeSource : IBackupSourceBoundaryView
    {
        public bool ContainsFile(string path) => true;
        public bool MayIntersectDirectory(string path) => true;
    }

    private sealed class SelectedSource(string selected) : IBackupSourceBoundaryView
    {
        public bool ContainsFile(string path) => path.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase);
        public bool MayIntersectDirectory(string path) => path == "." || path.Equals(selected, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer, false)]
    [DataRow(ConsistencyIntent.Require, false)]
    [DataRow(ConsistencyIntent.Prefer, true)]
    [DataRow(ConsistencyIntent.Require, true)]
    public async Task IncompleteObservationCannotBecomeOrdinaryAfterBoundaryProjection(ConsistencyIntent intent, bool truncateIssues)
    {
        using var world = TemporaryWorld.Create();
        var root = Path.GetDirectoryName(world.WorldPath)!;
        Directory.CreateDirectory(Path.Combine(root, "Included"));
        var excluded = Directory.CreateDirectory(Path.Combine(root, "ZExcluded")).FullName;
        for (var index = 0; index < 17; index++) excluded = Directory.CreateDirectory(Path.Combine(excluded, "d")).FullName;
        if (truncateIssues)
            for (var index = 0; index < 40; index++)
            {
                var missing = Directory.CreateDirectory(Path.Combine(root, "ZZMissing" + index.ToString("D2"))).FullName;
                File.WriteAllText(Path.Combine(missing, "level.dat_old"), "marker");
            }
        var raw = World.WorldObserver.Observe(root);
        Assert.IsFalse(raw.IsComplete);
        var fixture = Activate(); var sends = 0;
        fixture.Services.KnotLink.OnSendAsync = (_, _, _) => { sends++; return Task.CompletedTask; };
        var (config, folder) = Snapshots(root);
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config,
            [new(folder, new SelectedSource("Included"))], intent), fixture.Invocation);
        var request = new BackupConsistencyRequest(config, folder, intent) { CoordinationOnly = true };
        if (intent == ConsistencyIntent.Require)
            await Assert.ThrowsExactlyAsync<IOException>(() => run.AcquireSourceAsync(request, fixture.Invocation).AsTask());
        else
        {
            await using var lease = await run.AcquireSourceAsync(request, fixture.Invocation);
            Assert.IsTrue(lease.Diagnostics.Any(d => d.Code == "minerewind.world_identity_unresolved"));
        }
        Assert.AreEqual(0, sends);
        await run.CompleteAsync(new(OperationOutcome.Blocked, [], BackupTerminalDispatch.NotRequired), default);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer, false, false)]
    [DataRow(ConsistencyIntent.Require, false, false)]
    [DataRow(ConsistencyIntent.Prefer, true, false)]
    [DataRow(ConsistencyIntent.Require, true, false)]
    [DataRow(ConsistencyIntent.Prefer, true, true)]
    [DataRow(ConsistencyIntent.Require, true, true)]
    public async Task OneActiveWorldInAMixedSourceCoordinatesOnlyItsSelectedWorld(ConsistencyIntent intent, bool scoped, bool excludedOnline)
    {
        using var world = TemporaryWorld.Create(); using var occupied = world.AcquireSessionLock();
        var root = Path.GetDirectoryName(world.WorldPath)!;
        var other = Directory.CreateDirectory(Path.Combine(root, "Other")).FullName;
        File.WriteAllText(Path.Combine(other, "level.dat"), "marker");
        using var otherLock = excludedOnline
            ? File.Open(Path.Combine(other, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
        var fixture = Activate(); var saves = 0; var handshakes = 0;
        fixture.Services.KnotLink.OnSendAsync = async (name, arguments, _) =>
        {
            if (name == "handshake")
            {
                handshakes++; Assert.AreEqual("World", arguments["world"]);
                await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE", new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, fixture.Invocation);
            }
            if (name == "pre_hot_backup")
            {
                saves++; Assert.AreEqual("World", arguments["world"]);
                await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation);
            }
        };
        var (config, folder) = Snapshots(root);
        var (_, secondSource) = Snapshots(world.WorldPath);
        config = config with { Folders = [folder, secondSource] };
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config,
            [new(folder, scoped ? new SelectedSource("World") : new WholeSource()), new(secondSource, new WholeSource())], intent), fixture.Invocation);
        Assert.IsTrue(run.RequiresExclusiveKnotLinkSession);
        foreach (var capturedSource in new[] { folder, folder, secondSource })
        {
            await using var lease = await run.AcquireSourceAsync(new(config, capturedSource, intent)
                { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation);
            Assert.AreEqual(capturedSource.Path, lease.SourcePath); Assert.IsEmpty(lease.Diagnostics);
            Assert.AreEqual(intent == ConsistencyIntent.Require, lease.CaptureCancellation.CanBeCanceled);
            run.ReportSourceReadCompleted(capturedSource.FolderId);
        }
        Assert.AreEqual(1, handshakes); Assert.AreEqual(1, saves);
        await run.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.Submitted), default);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer, false)]
    [DataRow(ConsistencyIntent.Require, false)]
    [DataRow(ConsistencyIntent.Prefer, true)]
    [DataRow(ConsistencyIntent.Require, true)]
    public async Task MixedSourceWithoutUniqueOnlineWorldDoesNotSendProtocolRequests(ConsistencyIntent intent, bool bothOnline)
    {
        using var world = TemporaryWorld.Create();
        var root = Path.GetDirectoryName(world.WorldPath)!;
        var other = Directory.CreateDirectory(Path.Combine(root, "Other")).FullName;
        File.WriteAllText(Path.Combine(other, "level.dat"), "marker");
        using var firstLock = bothOnline ? world.AcquireSessionLock() : null;
        using var secondLock = bothOnline ? File.Open(Path.Combine(other, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
        var fixture = Activate(); var sends = 0;
        fixture.Services.KnotLink.OnSendAsync = (_, _, _) => { sends++; return Task.CompletedTask; };
        var (config, folder) = Snapshots(root);
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config, [new(folder, new WholeSource())], intent), fixture.Invocation);
        Assert.IsFalse(run.RequiresExclusiveKnotLinkSession);
        var request = new BackupConsistencyRequest(config, folder, intent) { CoordinationOnly = true };
        if (bothOnline && intent == ConsistencyIntent.Require)
            await Assert.ThrowsExactlyAsync<IOException>(() => run.AcquireSourceAsync(request, fixture.Invocation).AsTask());
        else
        {
            await using var lease = await run.AcquireSourceAsync(request, fixture.Invocation);
            Assert.AreEqual(bothOnline, lease.Diagnostics.Any(d => d.Code == "minerewind.consistency_multiple_active_worlds"));
            Assert.IsFalse(lease.CaptureCancellation.CanBeCanceled);
        }
        Assert.AreEqual(0, sends);
        await run.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.NotRequired), default);
    }

    [TestMethod]
    [DataRow(WorkspaceOperationKind.Restore)]
    [DataRow(WorkspaceOperationKind.Checkout)]
    [DataRow(WorkspaceOperationKind.Merge)]
    public async Task MissingPrimaryMetadataAndMultipleWorldsCannotBypassOccupiedWorld(WorkspaceOperationKind kind)
    {
        using var world = TemporaryWorld.Create();
        using var held = world.AcquireSessionLock();
        var fixture = Activate(); fixture.Services.KnotLink.IsAvailable = false;
        File.Move(Path.Combine(world.WorldPath, "level.dat"), Path.Combine(world.WorldPath, "level.dat_old"));
        var saves = Path.GetDirectoryName(world.WorldPath)!;
        var other = Directory.CreateDirectory(Path.Combine(saves, "Other")).FullName;
        File.WriteAllText(Path.Combine(other, "level.dat"), "marker");
        foreach (var path in new[] { world.WorldPath, saves })
        {
            var (config, folder) = Snapshots(path); var calls = 0;
            var result = await fixture.Plugin.CoordinateAsync(new(config, [folder], "history", Guid.NewGuid(), kind,
                _ => { calls++; return ValueTask.FromResult(OperationOutcome.Success); }), fixture.Invocation);
            Assert.AreEqual(OperationOutcome.Blocked, result.Outcome); Assert.AreEqual(0, calls);
            var error = await Assert.ThrowsAsync<Exception>(() => fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require)
                { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation).AsTask());
            Assert.IsTrue(error is IOException or InvalidOperationException);
        }
    }

    [TestMethod]
    public async Task MixedSourceLockExclusionsAreWorldRelativeAndOrdinaryScopesStayOrdinary()
    {
        using var world = TemporaryWorld.Create();
        var root = Path.GetDirectoryName(world.WorldPath)!;
        var ordinary = Directory.CreateDirectory(Path.Combine(root, "notes")).FullName;
        File.WriteAllText(Path.Combine(ordinary, "session.lock"), "business data");
        var fixture = Activate(); var (config, folder) = Snapshots(root);
        var policy = await fixture.Plugin.ResolveAsync(new FilePolicyRequest(config, folder), fixture.Invocation);
        Assert.IsTrue(policy.TypedRequiredExclusions.Any(r => r.Pattern == "World/**/session.lock"));
        Assert.IsFalse(policy.TypedRequiredExclusions.Any(r => r.Pattern.StartsWith("notes/", StringComparison.Ordinal)));
        var (ordinaryConfig, ordinaryFolder) = Snapshots(ordinary);
        await using (await fixture.Plugin.AcquireAsync(new(ordinaryConfig, ordinaryFolder, ConsistencyIntent.Require)
            { CoordinationOnly = true }, fixture.Invocation)) { }
        var (plainConfig, plain) = Snapshots(Directory.CreateDirectory(Path.Combine(root, "plain")).FullName);
        await using var lease = await fixture.Plugin.AcquireAsync(new(plainConfig, plain, ConsistencyIntent.Require)
            { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation);
        Assert.IsEmpty(lease.Diagnostics);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer)]
    [DataRow(ConsistencyIntent.Require)]
    public async Task RunReusesSaveAndDeadlineDoesNotRestartAcrossSourceReacquisition(ConsistencyIntent intent)
    {
        using var world = TemporaryWorld.Create(); using var held = world.AcquireSessionLock();
        var fixture = Activate(); var clock = new CoordinationTestClock(); fixture.Plugin.CoordinationClock = clock;
        var saves = 0;
        fixture.Services.KnotLink.OnSendAsync = async (name, _, _) =>
        {
            if (name == "handshake") await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE",
                new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, fixture.Invocation);
            if (name == "pre_hot_backup") { saves++; await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation); }
        };
        var (config, folder) = Snapshots(world.WorldPath);
        var (_, ordinary) = Snapshots(Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(world.WorldPath)!, "ordinary")).FullName);
        config = config with { Folders = [folder, ordinary] };
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config, [new(folder, new WholeSource()), new(ordinary, new WholeSource())], intent), fixture.Invocation);
        Assert.IsTrue(run.RequiresExclusiveKnotLinkSession);
        await using (await run.AcquireSourceAsync(new(config, folder, intent) { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation)) { }
        clock.Advance(TimeSpan.FromSeconds(100));
        await using (await run.AcquireSourceAsync(new(config, folder, intent) { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation)) { }
        clock.Advance(TimeSpan.FromSeconds(51));
        Assert.AreEqual(1, saves);
        Assert.AreEqual(intent == ConsistencyIntent.Require, run.CaptureCancellation.IsCancellationRequested);
        Assert.IsTrue(run.Diagnostics.Any(d => d.Code == "minerewind.coordination_window_expired"));
        await using var unrelated = await run.AcquireSourceAsync(new(config, ordinary, intent) { CoordinationOnly = true }, fixture.Invocation);
        Assert.IsFalse(unrelated.CaptureCancellation.CanBeCanceled, "World deadline must not cancel an ordinary Source in the same run.");
        await run.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.Submitted), default);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    public async Task UncertainSaveIsIsolatedUntilExplicitIdleReset()
    {
        using var world = TemporaryWorld.Create(); using var held = world.AcquireSessionLock();
        var fixture = Activate(); using var cancellation = new CancellationTokenSource();
        fixture.Services.KnotLink.OnSendAsync = async (name, _, _) =>
        {
            if (name == "handshake") await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE",
                new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, fixture.Invocation);
            if (name == "pre_hot_backup") cancellation.Cancel();
        };
        var (config, folder) = Snapshots(world.WorldPath);
        await using (var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config,
            [new(folder, new WholeSource())], ConsistencyIntent.Require), fixture.Invocation))
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => run.AcquireSourceAsync(new(config, folder, ConsistencyIntent.Require)
                { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation with { OperationCancellation = cancellation.Token }).AsTask());
            Assert.AreEqual(OperationOutcome.Blocked, (await fixture.Plugin.ResetAsync(fixture.Invocation)).Outcome);
            await run.CompleteAsync(new(OperationOutcome.Canceled, [], BackupTerminalDispatch.Submitted), default);
        }
        Assert.AreEqual(BackupCoordinationState.NeedsReset, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
        var late = await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, late.Outcome);
        await using (var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config,
            [new(folder, new WholeSource())], ConsistencyIntent.Prefer), fixture.Invocation))
        {
            await using var weak = await run.AcquireSourceAsync(new(config, folder, ConsistencyIntent.Prefer) { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation);
            Assert.IsTrue(weak.Diagnostics.Any(d => d.Code == "minerewind.coordination_needs_reset"));
            await run.CompleteAsync(new(OperationOutcome.SuccessWithWarnings, [], BackupTerminalDispatch.NotRequired), default);
        }
        Assert.AreEqual(OperationOutcome.Success, (await fixture.Plugin.ResetAsync(fixture.Invocation)).Outcome);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    public async Task SealedInputsAndCompletedRunsCannotCancelCommittedWork()
    {
        using var world = TemporaryWorld.Create(); using var held = world.AcquireSessionLock();
        var fixture = Activate(); var clock = new CoordinationTestClock(); fixture.Plugin.CoordinationClock = clock;
        fixture.Services.KnotLink.OnSendAsync = async (name, _, _) =>
        {
            if (name == "handshake") await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE", new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, fixture.Invocation);
            if (name == "pre_hot_backup") await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation);
        };
        var (config, folder) = Snapshots(world.WorldPath);
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config, [new(folder, new WholeSource())], ConsistencyIntent.Require), fixture.Invocation);
        await using (await run.AcquireSourceAsync(new(config, folder, ConsistencyIntent.Require) { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation)) { }
        var cancellation = run.CaptureCancellation;
        run.ReportSourceReadCompleted(folder.FolderId);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsFalse(cancellation.IsCancellationRequested);
        Assert.IsFalse(run.Diagnostics.Any(d => d.Code == "minerewind.coordination_window_expired"));
        await run.CompleteAsync(new(OperationOutcome.NoChanges, [new(folder.FolderId, OperationOutcome.NoChanges, false)], BackupTerminalDispatch.Submitted), default);
        await run.CompleteAsync(new(OperationOutcome.Canceled, [], BackupTerminalDispatch.Failed), default);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer)]
    [DataRow(ConsistencyIntent.Require)]
    public async Task ChangedObservationRequiresReadmissionOrExplicitDegradationBeforeAnyExternalRequest(ConsistencyIntent intent)
    {
        using var world = TemporaryWorld.Create(); var fixture = Activate(); var calls = 0;
        fixture.Services.KnotLink.OnSendAsync = (_, _, _) => { calls++; return Task.CompletedTask; };
        var (config, folder) = Snapshots(world.WorldPath);
        await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config, [new(folder, new WholeSource())], intent), fixture.Invocation);
        Assert.IsFalse(run.RequiresExclusiveKnotLinkSession); Assert.AreEqual(0, calls);
        using var held = world.AcquireSessionLock();
        if (intent == ConsistencyIntent.Require)
            await Assert.ThrowsExactlyAsync<BackupCoordinationAdmissionRequiredException>(() => run.AcquireSourceAsync(new(config, folder, intent)
                { CoordinationOnly = true }, fixture.Invocation).AsTask());
        else
        {
            await using var weak = await run.AcquireSourceAsync(new(config, folder, intent) { CoordinationOnly = true }, fixture.Invocation);
            Assert.IsTrue(weak.Diagnostics.Any(d => d.Code == "minerewind.consistency_raw_source"));
        }
        Assert.AreEqual(0, calls);
        await run.CompleteAsync(new(OperationOutcome.Blocked, [], BackupTerminalDispatch.NotRequired), default);
        Assert.AreEqual(BackupCoordinationState.Ready, (await fixture.Plugin.GetStatusAsync(fixture.Invocation)).State);
    }

    [TestMethod]
    [DataRow(WorkspaceOperationKind.Restore)]
    [DataRow(WorkspaceOperationKind.Checkout)]
    [DataRow(WorkspaceOperationKind.Merge)]
    public async Task WorldRelockingDuringHostPreparationIsRejectedByReadOnlySafetyCheck(WorkspaceOperationKind kind)
    {
        using var world = TemporaryWorld.Create(); var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath); RestoreMutationSafetyCheck? check = null;
        var writes = 0; var sends = 0;
        fixture.Services.KnotLink.OnSendAsync = (_, _, _) => { sends++; return Task.CompletedTask; };
        var request = new RestoreCoordinatorRequest(config, [folder], "fixture", Guid.NewGuid(), kind, async token =>
        {
            Assert.IsNotNull(check);
            using var held = world.AcquireSessionLock();
            var diagnostics = await check(token);
            Assert.IsTrue(diagnostics.Any(d => d.Code == "minerewind.restore_world_still_occupied"));
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return OperationOutcome.Blocked;
            writes++; return OperationOutcome.Success;
        }) { RegisterMutationSafetyCheck = value => check = value };
        Assert.AreEqual(OperationOutcome.Blocked, (await fixture.Plugin.CoordinateAsync(request, fixture.Invocation)).Outcome);
        Assert.AreEqual(0, writes); Assert.AreEqual(0, sends);
    }

    [TestMethod]
    public async Task RestoreWaitingForChannelCannotHoldTheHandshakeNeededByItsBackupOwner()
    {
        using var world = TemporaryWorld.Create(); using var held = world.AcquireSessionLock();
        var fixture = Activate(); var (config, folder) = Snapshots(world.WorldPath);
        using var restoreCancellation = new CancellationTokenSource();
        using var channel = new SemaphoreSlim(0, 1);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prematureTerminals = 0;
        fixture.Services.KnotLink.OnSendAsync = async (name, _, _) =>
        {
            if (name == "handshake") await fixture.Plugin.ExecuteAsync("HANDSHAKE_RESPONSE",
                new Dictionary<string, string> { ["mod_version"] = "3.1.0" }, fixture.Invocation);
            if (name == "pre_hot_backup") await fixture.Plugin.ExecuteAsync("WORLD_SAVED", new Dictionary<string, string>(), fixture.Invocation);
            if (name == "restore_cancelled") prematureTerminals++;
        };
        var restore = fixture.Plugin.CoordinateAsync(new(config, [folder], "fixture", Guid.NewGuid(), WorkspaceOperationKind.Restore,
            _ => throw new AssertFailedException("No mutation before channel admission."))
        {
            AdmitKnotLinkChannelAsync = async token => { waiting.SetResult(); await channel.WaitAsync(token); }
        }, fixture.Invocation with { OperationCancellation = restoreCancellation.Token }).AsTask();
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var run = await fixture.Plugin.CreateSessionAsync(new(Guid.NewGuid(), config,
                [new(folder, new WholeSource())], ConsistencyIntent.Require), fixture.Invocation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var input = await run.AcquireSourceAsync(new(config, folder, ConsistencyIntent.Require)
                { CoordinationOnly = true, RunChannelAdmitted = true }, fixture.Invocation with { OperationCancellation = timeout.Token });
            Assert.IsEmpty(input.Diagnostics);
            await run.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.Submitted), default);
        }
        finally { restoreCancellation.Cancel(); await restore; }
        Assert.AreEqual(OperationOutcome.Canceled, (await restore).Outcome);
        Assert.AreEqual(0, prematureTerminals, "A restore that never entered the channel cannot send a terminal into another run.");
    }

    private sealed class CoordinationTestClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private TimeSpan _elapsed;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state, _elapsed + dueTime); _timers.Add(timer); return timer; }
        public void Advance(TimeSpan time)
        {
            _elapsed += time;
            foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.At <= _elapsed) { timer.Disposed = true; timer.Callback(timer.State); }
        }
        private sealed class Timer(CoordinationTestClock clock, TimerCallback callback, object? state, TimeSpan at) : ITimer
        {
            public readonly TimerCallback Callback = callback; public readonly object? State = state;
            public TimeSpan At = at; public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { At = clock._elapsed + dueTime; return !Disposed; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
