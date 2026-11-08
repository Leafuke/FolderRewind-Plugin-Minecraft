using System.Security.Cryptography;
using System.Buffers;

namespace MineRewind.World.Semantic;

public static class SemanticPaths
{
    public const int MaximumInventoryEntries = 65_536;
    public const int MaximumInventoryPathCharacters = 4 * 1024 * 1024;
    public static string Normalize(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative)
            || relative.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Invalid relative path: " + relative);
        return relative;
    }

    public static string Resolve(string root, string relative)
    {
        Normalize(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked paths are not allowed.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    public static void RequireDisjoint(string output, params string[] inputs)
    {
        output = Path.GetFullPath(output);
        foreach (var input in inputs)
        {
            var fullInput = Path.GetFullPath(input);
            if (Contains(output, fullInput) || Contains(fullInput, output))
                throw new IOException("Input and output directories must be disjoint.");
        }
        // Reject linked ancestors even when the leaf has not been created yet.
        for (var part = output; part is not null; part = Path.GetDirectoryName(part))
            if (Directory.Exists(part) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Output cannot have linked ancestors.");
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

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

internal static class SemanticStreams
{
    public const int BufferSize = 65536;
    public static string Hash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    public static string Copy(Stream source, Stream target, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(BufferSize, length));
                if (read == 0) throw new EndOfStreamException();
                target.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); length -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
