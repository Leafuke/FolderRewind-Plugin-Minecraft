namespace MineRewind.World;

internal sealed class WorldScanLimitException(string reason) : IOException(reason);

/// <summary>A call-owned budget, shared across directories/worlds. Empty directories count too.</summary>
internal sealed class WorldScanBudget(int maximumEntries = 100_000, int maximumPathCharacters = 4 * 1024 * 1024)
{
    private int _entries;
    private long _characters;
    internal bool Incomplete { get; private set; }
    internal void MarkIncomplete() => Incomplete = true;
    internal void Count(string root, string path, CancellationToken token, int depth = 0, int maximumDepth = 64)
    {
        token.ThrowIfCancellationRequested();
        if (++_entries > maximumEntries || (_characters += Path.GetRelativePath(root, path).Length) > maximumPathCharacters || depth > maximumDepth)
        { Incomplete = true; throw new WorldScanLimitException("World directory scan budget exceeded."); }
    }
    internal IEnumerable<string> Entries(string root, string directory, CancellationToken token, bool rejectLinks, int depth = 0)
    {
        token.ThrowIfCancellationRequested();
        IEnumerator<string> iterator;
        try { WorldPaths.RequireContained(root, directory); iterator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Incomplete = true; if (rejectLinks) throw; yield break; }
        using (iterator)
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool next;
            try { next = iterator.MoveNext(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Incomplete = true; if (rejectLinks) throw; yield break; }
            if (!next) yield break;
            var path = iterator.Current;
            Count(root, path, token, depth);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Incomplete = true; if (rejectLinks) throw; continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { Incomplete = true; if (rejectLinks) throw new IOException("World input contains a linked path."); continue; }
            yield return path;
        }
    }
    internal IEnumerable<string> Files(string root, string start, CancellationToken token, bool rejectLinks)
    {
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((start, 0));
        while (pending.TryPop(out var item))
            foreach (var path in Entries(root, item.Path, token, rejectLinks, item.Depth))
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) pending.Push((path, item.Depth + 1));
                else yield return path;
    }
}
