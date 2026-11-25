using FolderRewind.Plugin.Abstractions;
using MineRewind;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    private sealed class PeerSteps(Func<Guid, Guid, string, string, Guid?, CorrelatedStepResult> handle) : ICorrelatedOperationSteps
    {
        public ValueTask<CorrelatedStepResult> RequestAsync(Guid operationId, Guid folderId, string physicalRoot,
            string kind, Guid? releasesStepId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(handle(operationId, folderId, physicalRoot, kind, releasesStepId));
        }
    }

    [TestMethod]
    public async Task PhysicalMatchRejectsAnotherWorldWithSameNameAndIdentifiesContainerBindings()
    {
        using var world = TemporaryWorld.Create();
        using var unrelated = TemporaryWorld.Create();
        using var lockFile = world.AcquireSessionLock();
        var fixture = Activate();
        var (direct, _) = Snapshots(world.WorldPath);
        var (container, _) = Snapshots(Path.GetDirectoryName(world.WorldPath)!);
        fixture.Services.Configs.QueryResults.Add(direct);
        fixture.Services.Configs.QueryResults.Add(container with { ConfigId = "container" });

        var other = await fixture.Plugin.MatchAsync(unrelated.WorldPath, fixture.Invocation);
        Assert.IsTrue(other.Complete);
        Assert.IsEmpty(other.Targets);
        var exact = await fixture.Plugin.MatchAsync(world.WorldPath, fixture.Invocation);
        Assert.IsTrue(exact.Complete);
        Assert.HasCount(2, exact.Targets);
        Assert.IsFalse(exact.Targets.Single(t => t.ConfigId == direct.ConfigId).IsContainer);
        Assert.IsTrue(exact.Targets.Single(t => t.ConfigId == "container").IsContainer);

        fixture.Services.Configs.QueryResults.Add(Snapshots(Path.Combine(world.WorldPath, "missing-source")).Config);
        var incomplete = await fixture.Plugin.MatchAsync(world.WorldPath, fixture.Invocation);
        Assert.IsFalse(incomplete.Complete);
        Assert.IsEmpty(incomplete.Targets);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Require, true, false)]
    [DataRow(ConsistencyIntent.Require, false, false)]
    [DataRow(ConsistencyIntent.Prefer, false, false)]
    [DataRow(ConsistencyIntent.Require, true, true)]
    public async Task SourceSealRequiresSameFreezeAndContinuousLiveReadEvidence(ConsistencyIntent intent, bool continuous, bool expire)
    {
        using var world = TemporaryWorld.Create();
        using var held = world.AcquireSessionLock();
        var fixture = Activate();
        var clock = new CoordinationTestClock(); fixture.Plugin.CoordinationClock = clock;
        var (config, folder) = Snapshots(world.WorldPath);
        var operation = Guid.NewGuid();
        var freeze = Guid.NewGuid();
        var actions = new List<string>();
        var invocation = fixture.Invocation with { CorrelatedSteps = new PeerSteps((op, source, root, kind, releases) =>
        {
            Assert.AreEqual(operation, op);
            Assert.AreEqual(folder.FolderId, source);
            Assert.AreEqual(PhysicalWorldDirectory.Resolve(world.WorldPath), root);
            actions.Add(kind);
            if (kind == "saveAndFreeze") { Assert.IsNull(releases); return new(freeze, "frozen"); }
            Assert.AreEqual(freeze, releases);
            return new(Guid.NewGuid(), "thawed", continuous);
        }) };
        await using var run = await fixture.Plugin.CreateSessionAsync(new(operation, config, [new(folder, new WholeSource())], intent), invocation);
        Assert.IsFalse(run.RequiresExclusiveKnotLinkSession);
        await using var lease = await run.AcquireSourceAsync(new(config, folder, intent) { CoordinationOnly = true }, invocation);
        if (expire) clock.Advance(TimeSpan.FromSeconds(151));
        var sealing = (IAsyncSourceReadCompletion)run;
        if (intent == ConsistencyIntent.Require && (!continuous || expire))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => sealing.ReportSourceReadCompletedAsync(folder.FolderId, default).AsTask());
            await Assert.ThrowsExactlyAsync<IOException>(() => sealing.ReportSourceReadCompletedAsync(folder.FolderId, default).AsTask());
        }
        else
        {
            var evidence = await sealing.ReportSourceReadCompletedAsync(folder.FolderId, default);
            Assert.AreEqual(!continuous, evidence.Any(d => d.Code == "minerewind.freeze_unconfirmed"));
        }
        await run.CompleteAsync(new(OperationOutcome.Success, [], BackupTerminalDispatch.NotRequired), default);
        CollectionAssert.AreEqual(new[] { "saveAndFreeze", "thaw" }, actions);
    }

    [TestMethod]
    [DataRow(OperationOutcome.Success, true, OperationOutcome.Success)]
    [DataRow(OperationOutcome.Success, false, OperationOutcome.SuccessWithWarnings)]
    [DataRow(OperationOutcome.CommittedRecoveryRequired, false, OperationOutcome.CommittedRecoveryRequired)]
    [DataRow(OperationOutcome.RecoveryRequired, false, OperationOutcome.RecoveryRequired)]
    public async Task RestoreReleasesBeforeMutationAndRecoveryNeverRequestsOpen(OperationOutcome mutation,
        bool grantOpen, OperationOutcome expected)
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        var operation = Guid.NewGuid();
        var occupied = true;
        fixture.Plugin.ProbeWorldLock = path => new(occupied ? WorldLockState.InUse : WorldLockState.Released, path);
        var calls = new List<string>();
        var invocation = fixture.Invocation with { CorrelatedSteps = new PeerSteps((op, source, root, kind, releases) =>
        {
            Assert.AreEqual(operation, op);
            Assert.AreEqual(folder.FolderId, source);
            calls.Add(kind);
            if (kind == "saveAndExit") { occupied = false; return new(Guid.NewGuid(), "released"); }
            return new(Guid.NewGuid(), grantOpen ? "opened" : "failed");
        }) };
        var result = await fixture.Plugin.CoordinateAsync(new(config, [folder], "version", operation, WorkspaceOperationKind.Restore,
            _ =>
            {
                Assert.IsFalse(occupied); calls.Add("mutate"); return ValueTask.FromResult(mutation);
            }), invocation);
        Assert.AreEqual(expected, result.Outcome);
        CollectionAssert.AreEqual(mutation is OperationOutcome.RecoveryRequired or OperationOutcome.CommittedRecoveryRequired
            ? new[] { "saveAndExit", "mutate" } : new[] { "saveAndExit", "mutate", "open" }, calls);
    }
}
