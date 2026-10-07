namespace MineRewind.World.Semantic;

// Persistent v1 schema: independent of the probe scratch and the public Host API.
public sealed record WorldObjectReference(Guid ArtifactId, string Path, long Offset, long Length, string Sha256);
public sealed record WorldRecipeSegment(long FileOffset, WorldObjectReference Content);
public sealed record WorldFileRecipe(int Version, string Encoding, long Length, IReadOnlyList<WorldRecipeSegment> Segments);
public sealed record WorldStateFile(string Path, long Length, string Sha256,
    WorldObjectReference Recipe, WorldObjectReference? Index);
public sealed record WorldIncrementalState(int Version, string SourceId, string BoundaryFingerprint,
    IReadOnlyList<WorldStateFile> Files, IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> DeletedChunks,
    bool Checkpoint, long NewDataBytes, int ChangedFiles)
{
    public long ReferencedObjectBytes { get; init; }
    public long PinnedPackBytes { get; init; }
    public bool CompactionRecommended => PinnedPackBytes > 2m * ReferencedObjectBytes;
}

public interface IWorldObjectReader
{
    /// <summary>Returns just the referenced range, independently verified against its digest.</summary>
    Stream Open(WorldObjectReference reference, CancellationToken token);
    void ValidateDependency(Guid owner, Guid dependency);
    long GetPackLength(Guid artifactId);
    IReadOnlyCollection<Guid> GetDependencies(Guid artifactId);
}

public interface IWorldObjectWriter : IWorldObjectReader
{
    WorldObjectReference Store(Stream input, long length, bool metadata,
        IReadOnlyCollection<Guid> dependencies, CancellationToken token);
    bool Owns(Guid artifactId);
    void BeginRepack();
}
