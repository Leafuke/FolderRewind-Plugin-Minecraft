namespace MineRewind.World.Semantic;

public sealed class DirectoryWorldSource : IReadOnlyWorldSource
{
    public string Root { get; }
    public DirectoryWorldSource(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
        for (var part = Root; part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source cannot have linked ancestors.");
    }
    public IReadOnlyList<SourceFile> GetInventory(CancellationToken token)
    {
        var result = new List<SourceFile>();
        long pathCharacters = 0; var entries = 0;
        var pending = new Stack<string>(); pending.Push(Root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                pathCharacters += Path.GetRelativePath(Root, path).Length;
                if (++entries > SemanticPaths.MaximumInventoryEntries || pathCharacters > SemanticPaths.MaximumInventoryPathCharacters)
                    throw new IOException("Offline inventory exceeds the P1 metadata budget.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Links are not a complete offline view: " + path);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var relative = SemanticPaths.Normalize(Path.GetRelativePath(Root, path).Replace('\\', '/'));
                var info = new FileInfo(path);
                result.Add(new(relative, info.Length, info.LastWriteTimeUtc));
            }
        }
        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        if (result.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new IOException("Case-colliding source paths.");
        return result;
    }
    public Stream OpenRead(string relativePath) => new FileStream(SemanticPaths.Resolve(Root, relativePath),
        FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
}
