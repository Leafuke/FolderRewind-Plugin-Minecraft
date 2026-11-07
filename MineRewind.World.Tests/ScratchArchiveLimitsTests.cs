using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class ScratchArchiveLimitsTests
{
    [TestMethod]
    [DataRow("count")]
    [DataRow("bytes")]
    [DataRow("host-budget")]
    [DataRow("path-budget")]
    [DataRow("segments")]
    [DataRow("missing-fields")]
    [DataRow("coverage")]
    public void FirstInvalidRecordIsRejectedBeforeCreatingItsOutput(string error)
    {
        var digest = Convert.ToHexString(SHA256.HashData([]));
        var file = new StoredFile("a", 0, digest, FileEncoding.Raw, []);
        var files = 1; long bytes = 0;
        if (error is "bytes" or "host-budget") { file = file with { Length = 1 }; if (error == "host-budget") bytes = 1; }
        if (error == "count") files = 0;
        if (error == "path-budget") file = file with { RelativePath = new string('a', SemanticPaths.MaximumInventoryPathCharacters + 1) };
        if (error == "segments") file = file with { Segments = Enumerable.Repeat(new StoredSegment(0, 0, "objects/a", 0, digest), 4097).ToArray() };
        if (error == "missing-fields") file = file with { Segments = null! };
        if (error == "coverage") file = file with { Segments = [new(1, 0, "objects/a", 0, digest)] };
        var source = Input(file, files, bytes); var destination = new Scratch();
        Assert.ThrowsExactly<InvalidDataException>(() => ScratchArchive.Reconstruct(source, destination,
            limits: error == "host-budget" ? new(0) : null));
        Assert.AreEqual(0, destination.Outputs, "Quota and record validation must precede output creation.");
    }

    private static Scratch Input(StoredFile file, int files, long bytes)
    {
        var scratch = new Scratch();
        scratch.Data["files.jsonl"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, SemanticJson.Options) + "\n");
        scratch.Data["regions.jsonl"] = []; scratch.Data["diagnostics.jsonl"] = [];
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(scratch.Data[path]));
        scratch.Data["complete.json"] = JsonSerializer.SerializeToUtf8Bytes(new ScratchCompletion(1, "source",
            Hash("files.jsonl"), Hash("regions.jsonl"), Hash("diagnostics.jsonl"),
            new(files, 0, bytes, 0, 0, 0, 0, 0, 0, 0, 0)), SemanticJson.Options);
        return scratch;
    }

    private sealed class Scratch : ISemanticScratch
    {
        public readonly Dictionary<string, byte[]> Data = [];
        public int Outputs;
        public Stream Create(string path) { Outputs++; return new MemoryStream(); }
        public Stream OpenRead(string path) => new MemoryStream(Data[path], false);
        public bool Exists(string path) => Data.ContainsKey(path);
    }
}
