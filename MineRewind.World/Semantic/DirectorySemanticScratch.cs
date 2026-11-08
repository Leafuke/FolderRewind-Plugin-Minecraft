namespace MineRewind.World.Semantic;

public sealed class DirectorySemanticScratch : ISemanticScratch
{
    public string Root { get; }
    public DirectorySemanticScratch(string root, bool create)
    {
        Root = Path.GetFullPath(root);
        SemanticPaths.RequireDisjoint(Root);
        if (create)
        {
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Scratch must be new.");
            Directory.CreateDirectory(Root);
        }
        else if (!Directory.Exists(Root)) throw new DirectoryNotFoundException(Root);
    }
    public Stream Create(string relativePath)
    {
        var path = SemanticPaths.Resolve(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
    }
    public Stream OpenRead(string relativePath) => new FileStream(SemanticPaths.Resolve(Root, relativePath),
        FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    public bool Exists(string relativePath) => File.Exists(SemanticPaths.Resolve(Root, relativePath));
}
