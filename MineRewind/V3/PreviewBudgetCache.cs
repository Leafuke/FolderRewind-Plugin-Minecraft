namespace MineRewind;

/// <summary>Session-owned cache. Access is guarded by the session lock; incomplete rasters outlive disposable samples.</summary>
internal sealed class PreviewBudgetCache(long limit = 128L * 1024 * 1024)
{
    private long _reserved;
    private sealed record Entry(object Key, object Value, long Bytes, int Priority);
    private readonly Dictionary<object, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry>[] _recent = [new(), new(), new()];
    public long Bytes { get; private set; }
    public T? Get<T>(object key) where T : class
    {
        if (!_entries.TryGetValue(key, out var node)) return null;
        var list = _recent[node.Value.Priority]; list.Remove(node); list.AddFirst(node); return (T)node.Value.Value;
    }
    public void Put(object key, object value, long bytes, int priority = 1)
    {
        Remove(key);
        if (bytes > limit - _reserved) return;
        _entries[key] = _recent[priority].AddFirst(new Entry(key, value, bytes, priority)); Bytes += bytes;
        Trim();
    }
    internal void SetReserved(long bytes)
    {
        if (bytes < 0 || bytes > limit) throw new InvalidOperationException("Invalid preview reservation.");
        _reserved = bytes; Trim();
    }
    private void Trim()
    {
        while (Bytes + _reserved > limit && _entries.Count > 0)
        {
            var candidate = _recent.First(list => list.Count > 0).Last!;
            Remove(candidate.Value.Key);
        }
    }
    internal T? Take<T>(object key) where T : class
    { var value = Get<T>(key); RemoveCore(key, dispose: false); return value; }
    public void Remove(object key)
        => RemoveCore(key, dispose: true);
    private void RemoveCore(object key, bool dispose)
    {
        if (_entries.Remove(key, out var old))
        {
            Bytes -= old.Value.Bytes; _recent[old.Value.Priority].Remove(old);
            if (dispose && old.Value.Value is IDisposable resource) resource.Dispose();
        }
    }
    public void Clear()
    {
        foreach (var entry in _entries.Values) if (entry.Value.Value is IDisposable resource) resource.Dispose();
        _entries.Clear(); foreach (var list in _recent) list.Clear(); Bytes = 0;
    }
}
