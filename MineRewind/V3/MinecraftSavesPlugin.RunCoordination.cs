using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : IBackupRunCoordinationCapability
{
    private readonly object _runGate = new();
    private readonly HashSet<BackupRunSession> _runs = [];
    private readonly AsyncLocal<BackupRunSession?> _acquiringRun = new();
    private BackupRunSession? _protocolRun;
    private bool _coordinationNeedsReset;
    private int _preparingRuns;
    internal TimeProvider CoordinationClock { get; set; } = TimeProvider.System;

    public async ValueTask<IBackupRunCoordinationSession> CreateSessionAsync(BackupRunCoordinationRequest request, PluginInvocationContext context)
    {
        EnsureActivated(); ValidateKind(request.Config.Kind);
        if (request.OperationId == Guid.Empty || request.Sources.Count == 0
            || request.Sources.Select(s => s.Folder.FolderId).Distinct().Count() != request.Sources.Count
            || request.Sources.Any(s => !request.Config.Folders.Any(f => f.FolderId == s.Folder.FolderId)))
            throw new ArgumentException("Invalid backup run Sources.");
        lock (_runGate) _preparingRuns++;
        try
        {
            var session = await Task.Run(() => new BackupRunSession(this, request, context), context.OperationCancellation).ConfigureAwait(false);
            lock (_runGate)
            {
                if (_runs.Any(r => r.OperationId == request.OperationId)) throw new InvalidOperationException("Duplicate backup run.");
                _runs.Add(session);
            }
            return session;
        }
        finally { lock (_runGate) _preparingRuns--; }
    }

    public ValueTask<BackupCoordinationStatus> GetStatusAsync(PluginInvocationContext context)
    {
        context.OperationCancellation.ThrowIfCancellationRequested();
        lock (_runGate)
        {
            var busy = _preparingRuns > 0 || _runs.Count > 0 || _restoreSignalGate.CurrentCount == 0;
            return ValueTask.FromResult(new BackupCoordinationStatus(busy ? BackupCoordinationState.Busy
                    : _coordinationNeedsReset ? BackupCoordinationState.NeedsReset : BackupCoordinationState.Ready,
                busy ? [Diagnostic("minerewind.coordination_busy", DiagnosticSeverity.Information, "BackupConsistency", ("reason", "related_operation"))]
                    : _coordinationNeedsReset ? [Diagnostic("minerewind.coordination_needs_reset", DiagnosticSeverity.Warning, "BackupConsistency", ("reason", "unverifiable_protocol_state"))] : []));
        }
    }

    public ValueTask<PluginCommandResult> ResetAsync(PluginInvocationContext context)
    {
        EnsureActivated(); context.OperationCancellation.ThrowIfCancellationRequested();
        lock (_runGate)
        {
            if (_preparingRuns != 0 || _runs.Count != 0 || _restoreSignalGate.CurrentCount == 0 || _pendingHandshake is not null
                || _pendingWorldSaved is not null || _pendingWorldExited is not null || _pendingRejoin is not null)
                return ValueTask.FromResult(CommandFailure("minerewind.coordination_busy"));
            _coordinationNeedsReset = false;
            return ValueTask.FromResult(Success("Existing-protocol compatibility coordination was reset; old remote acknowledgements remain unverifiable."));
        }
    }

    private void MarkCoordinationUncertain()
    {
        lock (_runGate) _coordinationNeedsReset = true;
    }

    private sealed class BackupRunSession : IBackupRunCoordinationSession
    {
        private readonly MinecraftSavesPlugin _owner;
        private readonly BackupRunCoordinationRequest _request;
        private readonly Dictionary<Guid, BackupCoordinationSource> _sources;
        private readonly Dictionary<Guid, WorldObservation> _observations;
        private readonly HashSet<string> _saved = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _readCompleted = [];
        private readonly List<PluginDiagnostic> _diagnostics = [];
        private readonly CancellationTokenSource _captureCancellation = new();
        private readonly object _gate = new();
        private ITimer? _timer;
        private long? _startedAt;
        private string? _liveWorld;
        private bool _deadlineExceeded, _completed, _disposed;
        public Guid OperationId => _request.OperationId;
        public bool RequiresExclusiveKnotLinkSession { get; }
        public CancellationToken CaptureCancellation => _captureCancellation.Token;
        public IReadOnlyList<PluginDiagnostic> Diagnostics { get { lock (_gate) return _diagnostics.ToArray(); } }

        internal BackupRunSession(MinecraftSavesPlugin owner, BackupRunCoordinationRequest request, PluginInvocationContext context)
        {
            _owner = owner; _request = request;
            _sources = request.Sources.ToDictionary(s => s.Folder.FolderId);
            _observations = request.Sources.ToDictionary(s => s.Folder.FolderId, s => Observe(s, context.OperationCancellation));
            var active = _observations.Values.SelectMany(o => o.Worlds).DistinctBy(w => w.Path, StringComparer.OrdinalIgnoreCase)
                .Count(w => owner.ProbeWorldLock(w.Path).State == WorldLockState.InUse);
            lock (owner._runGate)
                RequiresExclusiveKnotLinkSession = active == 1 && _observations.Values.All(o => o.Kind != WorldObservationKind.WorldEvidenceUnresolved)
                    && context.HostServices.KnotLink.IsAvailable && !owner._coordinationNeedsReset;
        }

        private static WorldObservation Observe(BackupCoordinationSource source, CancellationToken token)
        {
            var observation = MinecraftWorldPathResolver.Observe(source.Folder.Path, token);
            try
            {
                var worlds = observation.Worlds.Where(w => Relevant(w.Path)).ToArray();
                // Traversal may have stopped before any relevant world was visited. Diagnostics are
                // bounded and may name an excluded subtree; neither filtering nor truncation proves completeness.
                var unresolved = !observation.IsComplete;
                var issues = unresolved ? observation.Issues : observation.Issues.Where(i => Relevant(i.Path)).ToArray();
                return new(unresolved ? WorldObservationKind.WorldEvidenceUnresolved : worlds.Length == 0
                    ? WorldObservationKind.Ordinary : WorldObservationKind.ResolvedWorlds, worlds, issues);
                bool Relevant(string path)
                {
                    var relative = Path.GetRelativePath(source.Folder.Path, path).Replace('\\', '/');
                    return source.Boundary.MayIntersectDirectory(relative)
                        || source.Boundary.ContainsFile(relative == "." ? "level.dat" : relative + "/level.dat");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { return observation with { Kind = WorldObservationKind.WorldEvidenceUnresolved }; }
        }

        public async ValueTask<IConsistencyLease> AcquireSourceAsync(BackupConsistencyRequest request, PluginInvocationContext context)
        {
            ObjectDisposedException.ThrowIf(_disposed || _completed, this);
            if (!request.CoordinationOnly || request.Config.ConfigId != _request.Config.ConfigId
                || request.Intent != _request.Intent || !_sources.TryGetValue(request.Folder.FolderId, out var source)
                || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(source.Folder.Path), Path.GetFullPath(request.Folder.Path)))
                throw new InvalidOperationException("Source is outside the backup run.");
            var observation = Observe(source, context.OperationCancellation);
            string[] runWorlds;
            lock (_gate)
            {
                _observations[source.Folder.FolderId] = observation;
                runWorlds = _observations.Values.SelectMany(o => o.Worlds).Select(w => w.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            if (observation.Issues.Any(i => i.Code == "primary_metadata_missing"))
                lock (_gate)
                    if (!_diagnostics.Any(d => d.Code == "minerewind.primary_metadata_missing" && d.Arguments.GetValueOrDefault("folderId") == request.Folder.FolderId.ToString("D")))
                        _diagnostics.Add(Diagnostic("minerewind.primary_metadata_missing", DiagnosticSeverity.Warning, "BackupConsistency",
                            ("folderId", request.Folder.FolderId.ToString("D"))));
            var states = observation.Worlds.Select(w => (World: w, Probe: _owner.ProbeWorldLock(w.Path))).ToArray();
            if (observation.Kind == WorldObservationKind.Ordinary) return Lease([]);
            if (observation.Kind == WorldObservationKind.WorldEvidenceUnresolved || states.Any(s => s.Probe.State == WorldLockState.Unknown))
                return Degrade("minerewind.world_identity_unresolved");
            if (states.All(s => s.Probe.State == WorldLockState.Released)) return Lease([]);
            var runActive = runWorlds.Count(w => _owner.ProbeWorldLock(w).State == WorldLockState.InUse);
            if (states.Count(s => s.Probe.State == WorldLockState.InUse) != 1 || runActive > 1)
                return Degrade("minerewind.consistency_multiple_active_worlds");
            lock (_owner._runGate)
            {
                if (_owner._coordinationNeedsReset) return Degrade("minerewind.coordination_needs_reset");
                if (_owner._protocolRun is not null && _owner._protocolRun != this) return Degrade("minerewind.coordination_busy");
            }
            if (!context.HostServices.KnotLink.IsAvailable) return Degrade("minerewind.consistency_raw_source");
            if (!request.RunChannelAdmitted)
            {
                if (_request.Intent == ConsistencyIntent.Prefer) return Degrade("minerewind.consistency_raw_source");
                throw new BackupCoordinationAdmissionRequiredException();
            }
            lock (_owner._runGate) _owner._protocolRun = this;
            var active = states.Single(s => s.Probe.State == WorldLockState.InUse).World.Path;
            lock (_gate)
            {
                _readCompleted.Remove(request.Folder.FolderId);
                if (_saved.Count > 0 && !_saved.Contains(active)) return Degrade("minerewind.consistency_multiple_active_worlds");
                _liveWorld = active;
                if (_startedAt is { } started && _owner.CoordinationClock.GetElapsedTime(started) >= TimeSpan.FromSeconds(150)) Deadline();
                if (_deadlineExceeded) return Degrade("minerewind.coordination_window_expired");
                if (_saved.Contains(active)) return Lease([], coordinated: true);
            }
            var previous = _owner._acquiringRun.Value; _owner._acquiringRun.Value = this;
            try
            {
                var result = await _owner.CoordinateBackupWorldAsync(request, active, context).ConfigureAwait(false);
                if (result.SaveAcknowledged)
                {
                    lock (_gate) _saved.Add(active);
                }
                return Lease(result.Diagnostics, coordinated: result.SaveAcknowledged);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lock (_gate) _diagnostics.Add(Diagnostic(error is TimeoutException ? "minerewind.consistency_save_timeout"
                    : "minerewind.consistency_coordination_failed", DiagnosticSeverity.Error, "BackupConsistency",
                    ("folderId", request.Folder.FolderId.ToString("D")), ("message", error.Message)));
                throw;
            }
            finally { _owner._acquiringRun.Value = previous; }

            IConsistencyLease Lease(IReadOnlyList<PluginDiagnostic> diagnostics, bool coordinated = false)
                => new MinecraftConsistencyLease(request.Folder.Path, diagnostics, null)
                { CaptureCancellation = coordinated && _request.Intent == ConsistencyIntent.Require ? _captureCancellation.Token : default };
            IConsistencyLease Degrade(string code)
            {
                if (_request.Intent == ConsistencyIntent.Require)
                {
                    lock (_gate) _diagnostics.Add(Diagnostic(code, DiagnosticSeverity.Error, "BackupConsistency", ("folderId", request.Folder.FolderId.ToString("D"))));
                    throw new IOException(code);
                }
                return Lease([Diagnostic(code, DiagnosticSeverity.Warning, "BackupConsistency", ("folderId", request.Folder.FolderId.ToString("D")))]);
            }
        }

        internal void SavingStarted()
        {
            lock (_gate)
            {
                _startedAt ??= _owner.CoordinationClock.GetTimestamp();
                _timer ??= _owner.CoordinationClock.CreateTimer(_ => Deadline(), null,
                    TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
            }
        }

        private void Deadline()
        {
            lock (_gate)
            {
                if (_completed || _disposed || _deadlineExceeded || _observations.Where(p => p.Value.Worlds
                    .Any(w => StringComparer.OrdinalIgnoreCase.Equals(w.Path, _liveWorld))).All(p => _readCompleted.Contains(p.Key))) return;
                _deadlineExceeded = true;
                foreach (var source in _observations.Where(p => p.Value.Worlds.Any(w => StringComparer.OrdinalIgnoreCase.Equals(w.Path, _liveWorld))
                    && !_readCompleted.Contains(p.Key)))
                    _diagnostics.Add(Diagnostic("minerewind.coordination_window_expired",
                        _request.Intent == ConsistencyIntent.Require ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "BackupConsistency", ("folderId", source.Key.ToString("D"))));
            }
            if (_request.Intent == ConsistencyIntent.Require)
                try { _captureCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (AggregateException)
                { lock (_gate) _diagnostics.Add(Diagnostic("minerewind.coordination_cancellation_callback_failed", DiagnosticSeverity.Error, "BackupConsistency")); }
        }

        public void ReportSourceReadCompleted(Guid folderId)
        {
            if (!_sources.ContainsKey(folderId)) throw new ArgumentException("Unknown Source.");
            lock (_gate) _readCompleted.Add(folderId);
        }

        public ValueTask<IReadOnlyList<PluginDiagnostic>> CompleteAsync(BackupRunCoordinationCompletion completion, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_completed) return ValueTask.FromResult(Diagnostics);
                _completed = true; _timer?.Dispose();
            }
            var needsReset = false;
            lock (_owner._runGate)
            {
                if (_owner._protocolRun == this)
                {
                    if (completion.TerminalDispatch != BackupTerminalDispatch.Submitted) _owner._coordinationNeedsReset = true;
                    needsReset = _owner._coordinationNeedsReset;
                    _owner._protocolRun = null;
                }
                _owner._runs.Remove(this);
            }
            if (needsReset) lock (_gate) _diagnostics.Add(Diagnostic("minerewind.coordination_needs_reset", DiagnosticSeverity.Warning,
                "BackupConsistency", ("reason", "unverifiable_protocol_completion")));
            return ValueTask.FromResult(Diagnostics);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; _timer?.Dispose(); }
            lock (_owner._runGate)
            {
                if (_owner._protocolRun == this) { _owner._protocolRun = null; _owner._coordinationNeedsReset = true; }
                _owner._runs.Remove(this);
            }
            _captureCancellation.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
