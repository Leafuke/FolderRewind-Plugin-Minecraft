using System.Text.Json;

namespace MineRewind.World.Semantic;

/// <summary>Persistent v1 contract, deliberately separate from disposable WorldProbe scratch.</summary>
public sealed record WorldFullManifest(string Format, int Version, string SourceId, string FilesHash,
    string RegionsHash, string DiagnosticsHash, int Files, long Bytes, int Regions, int OpaqueRegions,
    int MissingExternalFiles, int UnclassifiedRegions);

public static class WorldFullFormat
{
    public const string FormatId = "com.folderrewind.minerewind/minecraft-world-full";
    public const string ManifestPath = "world-full.json";

    public static WorldFullManifest ReadManifest(ISemanticScratch storage)
    {
        using var input = storage.OpenRead(ManifestPath);
        if (input.Length > 65536) throw new InvalidDataException("Oversized Full manifest.");
        var m = JsonSerializer.Deserialize<WorldFullManifest>(input, SemanticJson.Options) ?? throw new InvalidDataException("Missing manifest.");
        if (m.Format != FormatId || m.Version != 1 || string.IsNullOrWhiteSpace(m.SourceId)
            || m.Files < 0 || m.Files > SemanticPaths.MaximumInventoryEntries || m.Bytes < 0)
            throw new InvalidDataException("Unsupported Full format.");
        return m;
    }

    public static void Materialize(ISemanticScratch storage, ISemanticScratch destination, string expectedSourceId, CancellationToken token,
        SemanticArchiveReadLimits? limits = null)
    {
        var m = ReadManifest(storage);
        if (m.SourceId != expectedSourceId) throw new InvalidDataException("Full representation belongs to a different Source.");
        ScratchArchive.Reconstruct(new FullReadView(storage, m), destination, token, limits);
    }

    private sealed class FullReadView(ISemanticScratch inner, WorldFullManifest m) : ISemanticScratch
    {
        public Stream Create(string path) => throw new NotSupportedException("Full format is immutable.");
        public bool Exists(string path) => path == "complete.json" || inner.Exists(path);
        public Stream OpenRead(string path)
        {
            if (path != "complete.json") return inner.OpenRead(path);
            var summary = new CaptureSummary(m.Files, m.Regions, m.Bytes, 0, 0, m.OpaqueRegions,
                m.MissingExternalFiles, 0, m.UnclassifiedRegions, 0, 0);
            return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new ScratchCompletion(1, m.SourceId,
                m.FilesHash, m.RegionsHash, m.DiagnosticsHash, summary), SemanticJson.Options), false);
        }
    }
}
