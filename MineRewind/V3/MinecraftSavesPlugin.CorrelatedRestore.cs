using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private async ValueTask<RestoreCoordinatorResult> CoordinateBoundRestoreAsync(RestoreCoordinatorRequest request,
        PluginInvocationContext context, ICorrelatedOperationSteps steps, CoordinatedWorld target, bool wasActive,
        RestoreMutationSafetyCheck checkMutationSafety, Func<bool> observedViolation)
    {
        var token = context.OperationCancellation;
        var root = PhysicalWorldDirectory.Resolve(target.WorldPath);
        var folder = target.Folder.FolderId;
        if (wasActive)
        {
            CorrelatedStepResult exit;
            try
            {
                exit = await steps.RequestAsync(request.OperationId, folder, root, "saveAndExit", null, token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                return Blocked("minerewind.restore_world_exit_timeout");
            }
            if (exit.StepId == Guid.Empty || exit.Outcome != "released") return Blocked("minerewind.restore_world_exit_timeout");
            var probe = await WaitForWorldReleaseAsync(root, token).ConfigureAwait(false);
            if (probe.State != WorldLockState.Released)
                return Blocked(probe.State == WorldLockState.InUse ? "minerewind.restore_world_still_occupied" : "minerewind.world_identity_unresolved");
        }

        var diagnostics = await checkMutationSafety(token).ConfigureAwait(false);
        if (diagnostics.Count != 0) return new(OperationOutcome.Blocked, diagnostics);
        // The registered check is also called by the Host at the actual mutation boundary.
        // An exceptional continuation has no authoritative reopen decision.
        var outcome = await request.ContinueMutationAsync(token).ConfigureAwait(false);
        if (!wasActive || observedViolation()
            || outcome is OperationOutcome.RecoveryRequired or OperationOutcome.CommittedRecoveryRequired)
            return new(outcome, []);

        try
        {
            // Only the Host can project engine commit/rollback evidence and grant this step.
            var open = await steps.RequestAsync(request.OperationId, folder, root, "open", null, context.PluginLifetime)
                .ConfigureAwait(false);
            if (open.StepId != Guid.Empty && open.Outcome is "opened" or "scriptStarted" or "abandoned")
                return new(outcome, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.PluginLifetime.IsCancellationRequested)
        {
            // A failed post-action cannot reverse a committed restore.
        }
        return new(outcome == OperationOutcome.Success ? OperationOutcome.SuccessWithWarnings : outcome,
            [Diagnostic("minerewind.restore_rejoin_failed", DiagnosticSeverity.Warning, "RestoreCoordinator")]);

        static RestoreCoordinatorResult Blocked(string code) =>
            new(OperationOutcome.Blocked, [Diagnostic(code, DiagnosticSeverity.Error, "RestoreCoordinator")]);
    }
}
