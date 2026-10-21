using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    internal Func<string, WorldLockProbeResult> ProbeWorldLock { get; set; } = path => MinecraftWorldLockProbe.Inspect(path);
    private sealed record CoordinatedWorld(FolderSnapshot Folder, string WorldPath);
    private static CoordinatedWorld[] ResolveWorlds(IEnumerable<FolderSnapshot> folders)
        => folders.Select(folder => (Folder: folder, World: ResolveWorldPath(folder.Path)))
            .Where(item => item.World is not null)
            .Select(item => new CoordinatedWorld(item.Folder, item.World!)).ToArray();
    private static PluginDiagnostic LockProbeDiagnostic(WorldLockProbeResult probe, DiagnosticSeverity severity, string capability)
        => Diagnostic("minerewind.world_lock_probe_failed", severity, capability,
            ("path", probe.LockPath), ("exceptionType", probe.ExceptionType ?? ""),
            ("hresult", probe.HResult ?? ""), ("message", probe.Message ?? ""));

    private KnotLinkTargetResolution ResolveActiveWorld(IReadOnlyList<ConfigSnapshot> configs)
    {
        var active = new List<KnotLinkTarget>();
        var diagnostics = new List<PluginDiagnostic>();
        foreach (var config in configs)
        foreach (var world in ResolveWorlds(config.Folders))
        {
            var probe = ProbeWorldLock(world.WorldPath);
            if (probe.State == WorldLockState.Unknown)
                diagnostics.Add(LockProbeDiagnostic(probe, DiagnosticSeverity.Error, "KnotLinkTargetResolution"));
            else if (probe.State == WorldLockState.InUse) active.Add(new(config.ConfigId, world.Folder.FolderId));
        }
        if (diagnostics.Count > 0) return new(null, diagnostics);
        return active.Count == 1 ? new(active[0], []) : new(null,
            [Diagnostic(active.Count == 0 ? "minerewind.command_active_world_not_found" : "minerewind.command_multiple_active_worlds",
                DiagnosticSeverity.Error, "KnotLinkTargetResolution")]);
    }

    public async ValueTask<RestoreCoordinatorResult> CoordinateAsync(RestoreCoordinatorRequest request, PluginInvocationContext context)
    {
        EnsureActivated(); ValidateKind(request.Config.Kind);
        var diagnostics = new List<PluginDiagnostic>();
        var worlds = ResolveWorlds(request.Folders);
        if (worlds.Length == 0) return new(await request.ContinueMutationAsync(context.OperationCancellation), diagnostics);
        var states = worlds.Select(world => (World: world, Probe: ProbeWorldLock(world.WorldPath))).ToArray();
        var unknown = states.Where(item => item.Probe.State == WorldLockState.Unknown).ToArray();
        if (unknown.Length > 0) return new(OperationOutcome.Blocked,
            unknown.Select(item => LockProbeDiagnostic(item.Probe, DiagnosticSeverity.Error, "RestoreCoordinator")).ToArray());
        var active = states.Where(item => item.Probe.State == WorldLockState.InUse).ToArray();
        if (active.Length > 1) return new(OperationOutcome.Blocked,
            [Diagnostic("minerewind.restore_multiple_active_worlds", DiagnosticSeverity.Error, "RestoreCoordinator")]);
        var selected = active.Length == 1 ? active[0].World : worlds[0];
        request = request with { Folders = [selected.Folder] };
        var worldWasActive = active.Length == 1;
        var signalGateHeld = false; var prepared = false; var safeToRejoin = false; var cancellationReported = false;
        var outcome = OperationOutcome.Blocked;
        try
        {
            try
            {
                if (worldWasActive)
                {
                    if (!context.HostServices.KnotLink.IsAvailable)
                        return new(OperationOutcome.Blocked,
                            [Diagnostic("minerewind.restore_knotlink_unavailable", DiagnosticSeverity.Error, "RestoreCoordinator")]);
                    await _restoreSignalGate.WaitAsync(context.OperationCancellation).ConfigureAwait(false);
                    signalGateHeld = true;
                    var compatible = await PerformModHandshakeAsync("restore", Path.GetFileName(selected.WorldPath), context).ConfigureAwait(false);
                    if (!compatible)
                    {
                        diagnostics.Add(Diagnostic("minerewind.restore_handshake_unavailable", DiagnosticSeverity.Error, "RestoreCoordinator"));
                        await ReportRestoreCancelledAsync(request, "no_mod", context).ConfigureAwait(false); cancellationReported = true;
                    }
                    else if (!await RequestWorldExitAsync(request, context).ConfigureAwait(false))
                    {
                        diagnostics.Add(Diagnostic("minerewind.restore_world_exit_timeout", DiagnosticSeverity.Error, "RestoreCoordinator"));
                        await ReportRestoreCancelledAsync(request, "timeout", context).ConfigureAwait(false); cancellationReported = true;
                    }
                    else
                    {
                        var released = await WaitForWorldReleaseAsync(selected.WorldPath, context.OperationCancellation).ConfigureAwait(false);
                        prepared = released.State == WorldLockState.Released;
                        if (!prepared)
                        {
                            diagnostics.Add(released.State == WorldLockState.Unknown
                                ? LockProbeDiagnostic(released, DiagnosticSeverity.Error, "RestoreCoordinator")
                                : Diagnostic("minerewind.restore_world_still_occupied", DiagnosticSeverity.Error, "RestoreCoordinator"));
                            await ReportRestoreCancelledAsync(request, released.State == WorldLockState.Unknown ? "world_state_unknown" : "world_occupied", context).ConfigureAwait(false);
                            cancellationReported = true;
                        }
                    }
                }
                if (!worldWasActive || prepared)
                {
                    var finalStates = worlds.Select(world => ProbeWorldLock(world.WorldPath)).ToArray();
                    foreach (var probe in finalStates.Where(probe => probe.State == WorldLockState.Unknown))
                        diagnostics.Add(LockProbeDiagnostic(probe, DiagnosticSeverity.Error, "RestoreCoordinator"));
                    if (finalStates.Any(probe => probe.State == WorldLockState.InUse))
                        diagnostics.Add(Diagnostic("minerewind.restore_world_still_occupied", DiagnosticSeverity.Error, "RestoreCoordinator"));
                    if (finalStates.All(probe => probe.State == WorldLockState.Released))
                    {
                        safeToRejoin = true;
                        outcome = await request.ContinueMutationAsync(context.OperationCancellation).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (context.OperationCancellation.IsCancellationRequested) { outcome = OperationOutcome.Canceled; }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic("minerewind.restore_coordination_failed", DiagnosticSeverity.Error, "RestoreCoordinator",
                    ("message", ex.Message), ("exceptionType", ex.GetType().FullName ?? ex.GetType().Name), ("hresult", $"0x{ex.HResult:X8}")));
                outcome = OperationOutcome.Failed;
            }
            if (worldWasActive && (!prepared || !safeToRejoin) && !cancellationReported)
                await ReportRestoreCancelledAsync(request, outcome == OperationOutcome.Canceled ? "cancelled" : "failed", context).ConfigureAwait(false);
            if (prepared && safeToRejoin && outcome is not (OperationOutcome.RecoveryRequired or OperationOutcome.CommittedRecoveryRequired))
            {
                try
                {
                    await ReportRestoreMutationFinishedAsync(request, outcome, context).ConfigureAwait(false);
                    var rejoin = await RequestWorldRejoinAsync(request, context).ConfigureAwait(false);
                    await ReportHotRestoreCompletedAsync(request, outcome, rejoin, context).ConfigureAwait(false);
                    if (rejoin != RejoinResult.Succeeded)
                    {
                        diagnostics.Add(Diagnostic("minerewind.restore_rejoin_failed", DiagnosticSeverity.Warning, "RestoreCoordinator"));
                        if (outcome == OperationOutcome.Success) outcome = OperationOutcome.SuccessWithWarnings;
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add(Diagnostic("minerewind.restore_rejoin_failed", DiagnosticSeverity.Warning, "RestoreCoordinator", ("message", ex.Message)));
                    if (outcome == OperationOutcome.Success) outcome = OperationOutcome.SuccessWithWarnings;
                }
            }
            return new(outcome, diagnostics);
        }
        finally
        {
            if (signalGateHeld)
            {
                Volatile.Write(ref _pendingWorldExited, null); Volatile.Write(ref _pendingRejoin, null);
                _restoreSignalGate.Release();
            }
        }
    }
}
