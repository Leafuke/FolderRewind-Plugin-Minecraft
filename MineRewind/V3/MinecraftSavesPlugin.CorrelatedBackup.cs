using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private sealed class CorrelatedBackupRun(
        MinecraftSavesPlugin owner, BackupRunCoordinationRequest run, ICorrelatedOperationSteps steps)
        : IBackupRunCoordinationSession, IAsyncSourceReadCompletion
    {
        private static readonly TimeSpan LiveReadWindow = TimeSpan.FromSeconds(150);
        private sealed record Freeze(string World, Guid StepId, long Started, CancellationTokenSource Deadline);
        private readonly SemaphoreSlim _gate = new(1);
        private readonly Dictionary<Guid, Freeze> _freezes = [];
        private readonly Dictionary<Guid, string> _sealFailures = [];
        private readonly List<PluginDiagnostic> _diagnostics = [];
        private bool _closed;
        public bool RequiresExclusiveKnotLinkSession => false;
        public CancellationToken CaptureCancellation => CancellationToken.None;
        public IReadOnlyList<PluginDiagnostic> Diagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }

        public async ValueTask<IConsistencyLease> AcquireSourceAsync(BackupConsistencyRequest request, PluginInvocationContext context)
        {
            var folder = request.Folder;
            var source = run.Sources.SingleOrDefault(s => s.Folder.FolderId == folder.FolderId);
            if (!request.CoordinationOnly || run.Config.ConfigId != request.Config.ConfigId || request.Intent != run.Intent
                || source is null || !StringComparer.Ordinal.Equals(Path.GetFullPath(folder.Path), Path.GetFullPath(source.Folder.Path)))
                throw new InvalidOperationException("Source is outside the correlated run.");
            await _gate.WaitAsync(context.OperationCancellation).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _sealFailures.Remove(folder.FolderId);
                var observed = BackupRunSession.Observe(source, context.OperationCancellation);
                if (observed.Issues.Any(i => i.Code == "primary_metadata_missing"))
                    AddDiagnostic(folder.FolderId, "minerewind.primary_metadata_missing", DiagnosticSeverity.Warning);
                var states = observed.Worlds.Select(w => (World: w, State: owner.ProbeWorldLock(w.Path).State)).ToArray();
                if (!observed.IsComplete || states.Any(s => s.State == WorldLockState.Unknown))
                    return Uncoordinated("minerewind.world_identity_unresolved");
                var active = states.Where(s => s.State == WorldLockState.InUse).ToArray();
                if (active.Length == 0 && !_freezes.ContainsKey(folder.FolderId)) return Lease();
                if (active.Length != 1) return Uncoordinated("minerewind.consistency_multiple_active_worlds");
                string world;
                try { world = PhysicalWorldDirectory.Resolve(active[0].World.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return Uncoordinated("minerewind.world_identity_unresolved"); }
                if (_freezes.TryGetValue(folder.FolderId, out var held))
                    return held.World == world && owner.CoordinationClock.GetElapsedTime(held.Started) < LiveReadWindow
                        ? Lease(held) : Uncoordinated("minerewind.coordination_window_expired");
                // A second Source must not claim or thaw another Source's outstanding freeze.
                if (_freezes.Values.Any(f => f.World == world)) return Uncoordinated("minerewind.coordination_busy");
                var started = owner.CoordinationClock.GetTimestamp();
                var deadline = new CancellationTokenSource(LiveReadWindow, owner.CoordinationClock);
                try
                {
                    CorrelatedStepResult result;
                    try
                    {
                        result = await steps.RequestAsync(run.OperationId, folder.FolderId, world,
                            "saveAndFreeze", null, context.OperationCancellation).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !context.OperationCancellation.IsCancellationRequested)
                    {
                        return Uncoordinated("minerewind.consistency_save_timeout");
                    }
                    if (result.StepId == Guid.Empty || result.Outcome != "frozen")
                        return Uncoordinated("minerewind.consistency_save_timeout");
                    held = new(world, result.StepId, started, deadline);
                    _freezes.Add(folder.FolderId, held);
                    return Lease(held);
                }
                finally
                {
                    if (!_freezes.ContainsKey(folder.FolderId)) deadline.Dispose();
                }
            }
            finally { _gate.Release(); }

            IConsistencyLease Lease(Freeze? freeze = null) => new MinecraftConsistencyLease(folder.Path, [])
            { CaptureCancellation = run.Intent == ConsistencyIntent.Require && freeze != null ? freeze.Deadline.Token : default };
            IConsistencyLease Uncoordinated(string code) => new MinecraftConsistencyLease(folder.Path, EvidenceLost(folder.FolderId, code));
        }

        public void ReportSourceReadCompleted(Guid folderId) =>
            throw new InvalidOperationException("Correlated Source completion must await continuous freeze evidence.");

        public async ValueTask<IReadOnlyList<PluginDiagnostic>> ReportSourceReadCompletedAsync(Guid folderId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (!_freezes.Remove(folderId, out var held))
                    return _sealFailures.TryGetValue(folderId, out var failure) ? EvidenceLost(folderId, failure) : [];
                using (held.Deadline)
                {
                    var inWindow = owner.CoordinationClock.GetElapsedTime(held.Started) < LiveReadWindow;
                    held.Deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                    CorrelatedStepResult receipt;
                    try
                    {
                        receipt = await steps.RequestAsync(run.OperationId, folderId, held.World,
                            "thaw", held.StepId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        return RejectSeal("minerewind.freeze_unconfirmed");
                    }
                    if (!inWindow) return RejectSeal("minerewind.coordination_window_expired");
                    if (receipt.StepId == Guid.Empty || receipt.Outcome != "thawed" || !receipt.Continuous)
                        return RejectSeal("minerewind.freeze_unconfirmed");
                    return [];
                }
            }
            finally { _gate.Release(); }

            IReadOnlyList<PluginDiagnostic> RejectSeal(string code)
            {
                _sealFailures[folderId] = code;
                return EvidenceLost(folderId, code);
            }
        }

        private IReadOnlyList<PluginDiagnostic> EvidenceLost(Guid folderId, string code)
        {
            var diagnostic = AddDiagnostic(folderId, code,
                run.Intent == ConsistencyIntent.Require ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning);
            if (run.Intent == ConsistencyIntent.Require) throw new IOException(code);
            return [diagnostic];
        }

        private PluginDiagnostic AddDiagnostic(Guid folderId, string code, DiagnosticSeverity severity)
        {
            var id = folderId.ToString("D");
            lock (_diagnostics)
            {
                var existing = _diagnostics.FirstOrDefault(d => d.Code == code && d.Arguments.GetValueOrDefault("folderId") == id);
                if (existing is not null) return existing;
                var diagnostic = Diagnostic(code, severity, "BackupConsistency", ("folderId", id));
                _diagnostics.Add(diagnostic);
                return diagnostic;
            }
        }

        public async ValueTask<IReadOnlyList<PluginDiagnostic>> CompleteAsync(BackupRunCoordinationCompletion completion,
            CancellationToken cancellationToken)
        {
            await DisposeAsync().ConfigureAwait(false);
            return Diagnostics;
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_closed) return;
                _closed = true;
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var (folderId, held) in _freezes)
                {
                    held.Deadline.Dispose();
                    try
                    {
                        await steps.RequestAsync(run.OperationId, folderId, held.World, "thaw", held.StepId, cleanup.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        lock (_diagnostics) _diagnostics.Add(Diagnostic("minerewind.freeze_unconfirmed", DiagnosticSeverity.Warning,
                            "BackupConsistency", ("folderId", folderId.ToString("D"))));
                    }
                }
                _freezes.Clear();
            }
            finally { _gate.Release(); }
        }
    }
}
