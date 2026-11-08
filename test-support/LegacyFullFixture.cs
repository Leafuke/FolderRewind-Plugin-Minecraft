using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MineRewind.TestSupport;

// Independently authored v1 schema and literal Anvil bytes. No production capture, export,
// manifest DTO or region writer constructs this compatibility oracle.
internal static class LegacyFullFixture
{
    internal static (Dictionary<string, byte[]> Artifact, Dictionary<string, byte[]> Expected) Create(string sourceId)
    {
        byte[] nbt = [10, 0, 0, 0];
        var region = new byte[8201]; region[2] = 2; region[3] = 1;
        region[8195] = 5; region[8196] = 3; nbt.CopyTo(region, 8197);
        var external = new byte[8197]; external[2] = 2; external[3] = 1;
        external[8195] = 1; external[8196] = 131;
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["level.dat"] = [42], ["region/r.0.0.mca"] = region,
            ["region/r.1.0.mca"] = external, ["region/c.32.0.mcc"] = nbt
        };
        var artifact = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["objects/raw.bin"] = expected["level.dat"], ["objects/payload.bin"] = nbt,
            ["objects/residual.bin"] = region[..8197], ["objects/external-region.bin"] = external,
            ["objects/external-payload.bin"] = nbt, ["regions.jsonl"] = [], ["diagnostics.jsonl"] = []
        };
        object Segment(long offset, byte[] bytes, string path) => new
        { FileOffset = offset, Length = bytes.LongLength, ObjectPath = path, ObjectOffset = 0L, Sha256 = Hash(bytes) };
        var lines = expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(new
        {
            RelativePath = pair.Key, Length = pair.Value.LongLength, Sha256 = Hash(pair.Value),
            Encoding = pair.Key == "region/r.0.0.mca" ? "SegmentedRegion" : "Raw",
            Segments = pair.Key switch
            {
                "level.dat" => new[] { Segment(0, pair.Value, "objects/raw.bin") },
                "region/r.0.0.mca" => [Segment(0, region[..8197], "objects/residual.bin"), Segment(8197, nbt, "objects/payload.bin")],
                "region/r.1.0.mca" => [Segment(0, pair.Value, "objects/external-region.bin")],
                _ => [Segment(0, pair.Value, "objects/external-payload.bin")]
            }
        }));
        artifact["files.jsonl"] = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        artifact["world-full.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Format = "com.folderrewind.minerewind/minecraft-world-full", Version = 1, SourceId = sourceId,
            FilesHash = Hash(artifact["files.jsonl"]), RegionsHash = Hash([]), DiagnosticsHash = Hash([]),
            Files = expected.Count, Bytes = expected.Values.Sum(bytes => bytes.LongLength),
            Regions = 0, OpaqueRegions = 0, MissingExternalFiles = 0, UnclassifiedRegions = 2
        }));
        return (artifact, expected);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
