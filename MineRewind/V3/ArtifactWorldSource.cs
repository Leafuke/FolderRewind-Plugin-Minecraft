using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind;

/// <summary>One fixed Host inventory. Stream identities are verified again by the domain capture.</summary>
internal sealed class ArtifactWorldSource : IReadOnlyWorldSource
{
    private readonly IArtifactReadService _read;
    private readonly ArtifactContentHandle _content;
    private readonly CancellationToken _operationToken;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<SourceFile> _files;
    private readonly HashSet<string> _paths;

    internal ArtifactWorldSource(IArtifactReadService read, ArtifactContentHandle content, IReadOnlyList<ArtifactFileEntry> files, CancellationToken token)
    {
        if (files.Count > SemanticPaths.MaximumInventoryEntries
            || files.Sum(f => (long)f.RelativePath.Length) > SemanticPaths.MaximumInventoryPathCharacters
            || files.Any(f => f.Length < 0 || f.Sha256 is not { Length: 64 } || !f.Sha256.All(Uri.IsHexDigit))
            || files.Sum(f => f.Length) > 16L * 1024 * 1024 * 1024) throw new IOException("Host input inventory exceeds the capture budget.");
        _read = read; _content = content; _operationToken = token;
        _files = Array.AsReadOnly(files.Select(f => new SourceFile(SemanticPaths.Normalize(f.RelativePath), f.Length, DateTime.UnixEpoch)
            { Sha256 = f.Sha256.ToUpperInvariant() }).ToArray());
        _paths = _files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        if (_paths.Count != _files.Count || _paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _paths.Count)
            throw new InvalidDataException("Host inventory contains colliding paths.");
    }
    public IReadOnlyList<SourceFile> GetInventory(CancellationToken token)
    { token.ThrowIfCancellationRequested(); _operationToken.ThrowIfCancellationRequested(); return _files; }
    public Stream OpenRead(string relativePath)
    {
        _operationToken.ThrowIfCancellationRequested();
        if (!_paths.Contains(relativePath)) throw new FileNotFoundException("Path is outside the Host capture inventory.", relativePath);
        return _read.OpenReadAsync(_content, relativePath, _operationToken).AsTask().GetAwaiter().GetResult();
    }
}
