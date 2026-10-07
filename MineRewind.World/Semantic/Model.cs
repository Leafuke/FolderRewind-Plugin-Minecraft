using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineRewind.World.Semantic;

public enum SemanticState { Present, Absent, Corrupt, Unsupported, Changing, Unavailable }
public enum FileEncoding { SegmentedRegion, OpaqueRegion, Raw }
public sealed record SourceFile(string RelativePath, long Length, DateTime LastWriteUtc);
public sealed record RegionAddress(string WorldId, string DimensionId, int X, int Z, ChunkComponentKind Component)
{
    [JsonIgnore] public string GroupKey => JsonSerializer.Serialize(new object[] { WorldId, DimensionId, X, Z });
    [JsonIgnore] public string DimensionKey => JsonSerializer.Serialize(new[] { WorldId, DimensionId });
    [JsonIgnore] public string SortKey => GroupKey + "/" + (int)Component;
}
public sealed record ChunkEntry(int Slot, int X, int Z, SemanticState State, uint Timestamp,
    byte Compression, bool External, string? ExternalPath, string? PayloadHash, string? Diagnostic)
{
    public string? ExternalStorageHash { get; init; }
}
public sealed record RegionEntry(string RelativePath, RegionAddress Address, string FileHash,
    SemanticState State, IReadOnlyList<ChunkEntry> Chunks, IReadOnlyList<string> Diagnostics);
public sealed record StoredSegment(long FileOffset, long Length, string ObjectPath, long ObjectOffset, string Sha256);
public sealed record StoredFile(string RelativePath, long Length, string Sha256, FileEncoding Encoding,
    IReadOnlyList<StoredSegment> Segments);
public sealed record CaptureSummary(int Files, int Regions, long Bytes, int PresentChunks, int ComparableChunks,
    int OpaqueRegions, int MissingExternalFiles, int OrphanExternalFiles, int UnclassifiedRegions,
    long DecodeAndCopyBufferBoundBytes, double ElapsedSeconds)
{
    public bool InputComplete => MissingExternalFiles == 0 && OpaqueRegions == 0 && UnclassifiedRegions == 0;
    public bool FullyComparable => OpaqueRegions == 0 && UnclassifiedRegions == 0 && PresentChunks == ComparableChunks;
}
public sealed record ScratchCompletion(int SchemaVersion, string SourceId, string FilesHash, string RegionsHash,
    string DiagnosticsHash, CaptureSummary Summary);
public sealed record ProbeDiagnostic(string RelativePath, string Code);
public sealed record VerificationResult(bool Success, int Files, long Bytes, IReadOnlyList<string> Errors);

public static class SemanticJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32
    };
}

/// <summary>A complete, frozen inventory; callers own world-level consistency.</summary>
public interface IReadOnlyWorldSource
{
    IReadOnlyList<SourceFile> GetInventory(CancellationToken token);
    Stream OpenRead(string relativePath);
}

/// <summary>Implementations own a fresh workspace. Nothing is accepted before completion.</summary>
public interface ISemanticScratch
{
    Stream Create(string relativePath);
    Stream OpenRead(string relativePath);
    bool Exists(string relativePath);
}
