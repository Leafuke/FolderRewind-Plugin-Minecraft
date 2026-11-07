using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

internal sealed record PreviewTileIdentity(string Root, string? FixedIdentity, string? World, string Layer, int? Height, double X, double Z, double Units, string? Mode, bool Overlay)
{
    internal static PreviewTileIdentity Of(SpatialPreviewTileRequest request) => new(Path.GetFullPath(request.Source.FixedInput?.RootPath ?? request.Source.Folder.Path), request.Source.FixedInput?.Identity, request.Source.WorldId, request.LayerId,
        request.MaximumHeight, request.OriginX, request.OriginY, request.UnitsPerPixel, request.ViewModeId, request.ShowStatusOverlay);
}

/// <summary>All methods run under the session lock. Waiting jobs hold metadata only; borrowed work remains charged until returned.</summary>
internal sealed class PreviewWorkScheduler<T>(long limit, PreviewBudgetCache cache) where T : class, IDisposable
{
    internal sealed class Job(PreviewTileIdentity key)
    {
        internal readonly PreviewTileIdentity Key = key;
        internal readonly CancellationTokenSource Cancellation = new();
        internal T? Work;
        internal long Bytes, Order;
        internal int Priority, Borrowers, Admissions;
        internal bool Removed, Final, Queued;
    }
    private sealed record View(long Revision, PreviewTileIdentity[] Tiles);
    private readonly Dictionary<Guid, View> _views = [];
    private readonly Dictionary<PreviewTileIdentity, Job> _jobs = [];
    private readonly HashSet<Job> _retired = [];
    private long _sequence;
    private bool _closed;
    private long MetadataReservation => Math.Min(192 * 1024, limit / 5);
    internal long RetainedBytes { get; private set; }
    internal int ActiveCount => _jobs.Values.Count(j => j.Work is not null) + _retired.Count(j => j.Work is not null);
    internal void Update(SpatialPreviewDemand demand)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (demand.ViewId == Guid.Empty || demand.Revision <= 0 || demand.Tiles.Count > 256) throw new ArgumentException("Invalid preview demand.");
        if (_views.TryGetValue(demand.ViewId, out var old))
        {
            if (demand.Revision < old.Revision) return;
            if (demand.Revision == old.Revision)
            {
                if (!old.Tiles.SequenceEqual(demand.Tiles.Select(PreviewTileIdentity.Of)))
                    throw new InvalidDataException("Preview revision content changed.");
                return;
            }
        }
        if (!_views.ContainsKey(demand.ViewId) && _views.Count >= 8) throw new InvalidDataException("Too many preview views.");
        var priorities = new Dictionary<PreviewTileIdentity, int>();
        var incoming = demand.Tiles.Select(PreviewTileIdentity.Of).ToArray();
        var views = _views.Where(v => v.Key != demand.ViewId).Select(v => v.Value).Append(new(demand.Revision, incoming)).ToArray();
        foreach (var view in views)
            for (var i = 0; i < view.Tiles.Length; i++)
            {
                var key = view.Tiles[i];
                priorities[key] = Math.Min(priorities.GetValueOrDefault(key, int.MaxValue), i);
            }
        if (priorities.Count > 256) throw new InvalidDataException("Too many preview tiles.");
        var metadata = 1024 + views.Sum(v => 128L + v.Tiles.Length * 16L)
            + priorities.Keys.Sum(k => 512L + k.Root.Length * 2 + (k.FixedIdentity?.Length ?? 0) * 2
                + (k.World?.Length ?? 0) * 2 + k.Layer.Length * 2 + (k.Mode?.Length ?? 0) * 2);
        if (metadata > MetadataReservation) throw new InvalidDataException("Preview demand metadata exceeds its budget.");
        foreach (var key in _jobs.Keys.Except(priorities.Keys).ToArray())
        {
            var job = _jobs[key]; _jobs.Remove(key); job.Removed = true; job.Cancellation.Cancel();
            if (job.Borrowers == 0) Destroy(job); else _retired.Add(job);
        }
        foreach (var (key, priority) in priorities)
        {
            if (!_jobs.TryGetValue(key, out var job)) _jobs.Add(key, job = new(key));
            job.Priority = priority;
        }
        // Views retain references to canonical job identities, never another set of full request/source snapshots.
        _views[demand.ViewId] = new(demand.Revision, incoming.Select(k => _jobs[k].Key).ToArray());
        PublishCharge();
    }
    internal Job Require(SpatialPreviewTileRequest request)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (!_views.TryGetValue(request.ViewId, out var view) || request.DemandRevision <= 0 || request.DemandRevision > view.Revision)
            throw new InvalidOperationException("Preview demand was not applied.");
        var key = PreviewTileIdentity.Of(request);
        if (!view.Tiles.Contains(key) || !_jobs.TryGetValue(key, out var job))
            throw new OperationCanceledException("Preview tile demand was released.");
        return job;
    }
    internal bool Fits(long bytes) => bytes + MetadataReservation <= limit;
    internal bool TryBorrow(Job job, long bytes, Func<T> create)
    {
        if (job.Removed) throw new OperationCanceledException("Preview demand was released.");
        if (job.Work is null)
        {
            // Demand alone does not enqueue work: the Host or provider may already have the finished raster.
            if (!job.Queued) { job.Queued = true; job.Order = ++_sequence; }
            var pending = _jobs.Values.Where(j => j.Queued)
                .OrderBy(j => j.Admissions).ThenBy(j => j.Priority).ThenBy(j => j.Order).FirstOrDefault();
            if (ActiveCount >= 4 || Charge() + bytes > limit || pending != job) return false;
            var cached = cache.Take<T>(job.Key);
            job.Bytes = bytes; PublishCharge();
            try { job.Work = cached ?? create(); job.Admissions++; job.Queued = false; }
            catch { job.Bytes = 0; job.Queued = false; PublishCharge(); throw; }
        }
        job.Borrowers++;
        return true;
    }
    internal void Return(Job job, bool final, bool cacheWork)
    {
        job.Final |= final;
        job.Borrowers--;
        if (job.Borrowers < 0) throw new InvalidOperationException("Preview borrow underflow.");
        if (job.Borrowers == 0 && (job.Removed || job.Final))
        {
            var work = job.Work; var bytes = job.Bytes;
            job.Work = null; job.Bytes = 0; job.Final = false;
            PublishCharge();
            if (!job.Removed && cacheWork && work is not null) cache.Put(job.Key, work, bytes);
            else work?.Dispose();
            if (job.Removed) { _retired.Remove(job); job.Cancellation.Dispose(); }
        }
        PublishCharge();
    }
    private long Charge() => (_closed ? 0 : MetadataReservation) + _jobs.Values.Concat(_retired).Sum(j => j.Bytes);
    private void PublishCharge()
    {
        RetainedBytes = Charge();
        cache.SetReserved(RetainedBytes);
    }
    private static void Destroy(Job job) { job.Work?.Dispose(); job.Work = null; job.Bytes = 0; job.Cancellation.Dispose(); }
    internal void Close()
    {
        _closed = true; _views.Clear();
        foreach (var job in _jobs.Values)
        { job.Removed = true; job.Cancellation.Cancel(); if (job.Borrowers == 0) Destroy(job); else _retired.Add(job); }
        _jobs.Clear(); PublishCharge();
    }
}
