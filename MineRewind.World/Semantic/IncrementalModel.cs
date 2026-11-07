namespace MineRewind.World.Semantic;

// Persistent v1 schema: independent of the probe scratch and the public Host API.
public sealed record WorldObjectReference(Guid ArtifactId, string Path, long Offset, long Length, string Sha256);
public sealed record WorldRecipeSegment(long FileOffset, WorldObjectReference Content);
public sealed record WorldFileRecipe(int Version, string Encoding, long Length, IReadOnlyList<WorldRecipeSegment> Segments);
public sealed record WorldStateFile(string Path, long Length, string Sha256,
    WorldObjectReference Recipe, WorldObjectReference? Index)
{
    // File-scoped deduplication means intervals never alias across different files.
    // This compact per-pack summary avoids reading every unchanged recipe for accounting.
    public IReadOnlyDictionary<Guid, long>? ReferencedBytesByPack { get; init; }
    public WorldSemanticSummary? SemanticSummary { get; init; }
}
/// <summary>Diagnostic cache only. Never authorizes restore, omission, deletion or integrity acceptance.</summary>
public sealed record WorldSemanticSummary(int ClassifierVersion, int OpaqueRegions, int CorruptChunks,
    int UnsupportedChunks, int MissingExternalFiles, int UnclassifiedRegions)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasWarnings => !IsValid || OpaqueRegions > 0 || CorruptChunks > 0 || UnsupportedChunks > 0 || MissingExternalFiles > 0 || UnclassifiedRegions > 0;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => ClassifierVersion == 1 && OpaqueRegions is >= 0 and <= 1 && UnclassifiedRegions is >= 0 and <= 1
        && CorruptChunks is >= 0 and <= 1024 && UnsupportedChunks is >= 0 and <= 1024 && MissingExternalFiles is >= 0 and <= 1024;
    internal static WorldSemanticSummary From(RegionEntry? index) => index is null ? new(1, 0, 0, 0, 0, 1)
        : new(1, index.State == SemanticState.Present ? 0 : 1, index.Chunks.Count(c => c.State == SemanticState.Corrupt),
            index.Chunks.Count(c => c.State == SemanticState.Unsupported), index.Chunks.Count(c => c.Diagnostic == "missing_external_file"), 0);
}
public sealed record WorldIncrementalState(int Version, string SourceId, string BoundaryFingerprint,
    IReadOnlyList<WorldStateFile> Files, IReadOnlyList<string> DeletedFiles, IReadOnlyList<string> DeletedChunks,
    bool Checkpoint, long NewDataBytes, int ChangedFiles)
{
    public long ReferencedObjectBytes { get; init; }
    public long PinnedPackBytes { get; init; }
    public int SemanticClassifierVersion { get; init; }
    public string? WorldRootsFingerprint { get; init; }
    public bool CompactionRecommended => PinnedPackBytes > 2m * ReferencedObjectBytes;
}

public interface IWorldObjectReader
{
    /// <summary>Returns just the referenced range, independently verified against its digest.</summary>
    Stream Open(WorldObjectReference reference, CancellationToken token);
    /// <summary>Sequential consumption validates at the last byte. Partial disposal is not verification.</summary>
    Stream OpenSequential(WorldObjectReference reference, CancellationToken token) => Open(reference, token);
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
    void BeginFile(long length) { }
}
