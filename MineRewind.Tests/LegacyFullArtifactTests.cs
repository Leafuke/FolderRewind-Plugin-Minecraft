using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.TestSupport;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    private static readonly ArtifactId LegacyArtifactId = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    [TestMethod]
    public async Task RetiredFullWriteIsBlockedBeforeReadingOrAllocatingResources()
    {
        using var temporary = new LegacyTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var read = new LegacyRead([]); var staging = new LegacyOutput();
        var result = await fixture.Plugin.TransformAsync(new(config, folder, "version", new("revision"),
            LegacyPrimary(config, folder), [], new Dictionary<string, JsonElement>(), read, staging, new LegacyProgress()), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome); Assert.IsNull(result.Patch);
        var diagnostic = result.Diagnostics.Single();
        Assert.AreEqual("minerewind.incremental_input_required", diagnostic.Code);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsFalse(string.IsNullOrEmpty(diagnostic.Arguments["en-US"]));
        Assert.IsFalse(string.IsNullOrEmpty(diagnostic.Arguments["zh-CN"]));
        Assert.AreEqual(0, read.Requests); Assert.AreEqual(0, staging.Allocations);
        Assert.AreEqual(0, temporary.DirectoryRequests); Assert.AreEqual(0, temporary.Reservations);
    }

    [TestMethod]
    public async Task LegacyFullMaterializerRestoresIndependentGoldenBytesAndReleasesInputSpace()
    {
        using var temporary = new LegacyTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var (artifact, expected) = LegacyFullFixture.Create(folder.FolderId.ToString("D"));
        var restored = new LegacyOutput { BeforeWrite = () => Assert.AreEqual(0, temporary.Reservations) };
        var result = await fixture.Plugin.MaterializeAsync(new(config, folder, "version", LegacyArtifactId,
            [LegacyPrimary(config, folder)], RestoreMode.Clean, RestoreMode.Clean, new LegacyRead(artifact), restored, new LegacyProgress()), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), restored.Files.Keys.ToArray());
        foreach (var pair in expected) CollectionAssert.AreEqual(pair.Value, restored.Files[pair.Key]);
        Assert.AreEqual(0, temporary.Reservations); Assert.IsEmpty(Directory.GetDirectories(temporary.Root));
    }

    [TestMethod]
    [DataRow("digest")] [DataRow("truncate")] [DataRow("source")] [DataRow("cancel")] [DataRow("reserve")]
    public async Task LegacyReadFailuresRejectInputAndReleaseOwnedResources(string failure)
    {
        using var temporary = new LegacyTemporaryStorage { RefuseReservation = failure == "reserve" };
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var (artifact, _) = LegacyFullFixture.Create(failure == "source" ? Guid.Empty.ToString("D") : folder.FolderId.ToString("D"));
        using var cancel = new CancellationTokenSource();
        var read = new LegacyRead(artifact) { WrongDigest = failure == "digest", Truncate = failure == "truncate",
            BeforeOpen = failure == "cancel" ? cancel.Cancel : null };
        var output = new LegacyOutput();
        var invocation = fixture.Invocation with { OperationCancellation = cancel.Token };
        async Task Execute() => await fixture.Plugin.MaterializeAsync(new(config, folder, "version", LegacyArtifactId,
            [LegacyPrimary(config, folder)], RestoreMode.Clean, RestoreMode.Clean, read, output, new LegacyProgress()), invocation);
        if (failure == "cancel") await Assert.ThrowsExactlyAsync<OperationCanceledException>(Execute);
        else if (failure == "reserve") await Assert.ThrowsExactlyAsync<IOException>(Execute);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(Execute);
        Assert.AreEqual(0, temporary.Reservations); Assert.IsEmpty(Directory.GetDirectories(temporary.Root));
        Assert.IsEmpty(output.Files);
    }

    [TestMethod]
    public async Task LegacyReadCleanupFailurePreservesTheOriginalOutputFailure()
    {
        using var temporary = new LegacyTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var (artifact, _) = LegacyFullFixture.Create(folder.FolderId.ToString("D"));
        Stream? held = null;
        var output = new LegacyOutput { BeforeWrite = () =>
        {
            held = File.OpenRead(Directory.GetFiles(temporary.Root, "raw.bin", SearchOption.AllDirectories).Single());
            throw new IOException("original output failure");
        } };
        try
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(async () => await fixture.Plugin.MaterializeAsync(new(config,
                folder, "version", LegacyArtifactId, [LegacyPrimary(config, folder)], RestoreMode.Clean, RestoreMode.Clean,
                new LegacyRead(artifact), output, new LegacyProgress()), fixture.Invocation));
            Assert.AreEqual("original output failure", error.Message);
            Assert.IsInstanceOfType<IOException>(error.Data["TemporaryArtifactCleanupError"]);
            Assert.AreEqual(0, temporary.Reservations);
        }
        finally { held?.Dispose(); }
    }

    [TestMethod]
    public async Task MetadataCountsOnlyComponentFilesIncludingInBoundaryPaperSiblings()
    {
        using var world = TemporaryWorld.Create(); WriteLegacyLevelDat(world.WorldPath, "Precise", 0, 42, 1);
        foreach (var relative in new[] { "region/r.0.0.mca", "entities/r.0.0.mca", "poi/r.0.0.mca", "unrelated/r.3.3.mca", "region/nested/r.4.4.mca" })
        {
            var path = Path.Combine(world.WorldPath, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, []);
        }
        var source = Path.GetDirectoryName(world.WorldPath)!;
        var nether = Path.Combine(source, Path.GetFileName(world.WorldPath) + "_nether", "DIM-1", "region");
        Directory.CreateDirectory(nether); File.WriteAllBytes(Path.Combine(nether, "r.1.0.mca"), []);
        var (config, folder) = Snapshots(source); var fixture = Activate();
        var result = await fixture.Plugin.ReadAsync(new(config, folder), fixture.Invocation);
        Assert.AreEqual("4", result.Fields.Single(f => f.Key == "regionFileCount").Value.Default);
    }

    private static BackupArtifactSnapshot LegacyPrimary(ConfigSnapshot config, FolderSnapshot folder) => new(LegacyArtifactId,
        new(new("com.folderrewind.minerewind"), "minecraft-world-full"), 1, new(new("com.folderrewind.minerewind"), "minecraft-world-materializer"),
        new("primary"), "unused", 0, ArtifactCompleteness.Complete, CoreCaptureMode.Full, config.ConfigId, folder.FolderId, []);
    private sealed class LegacyProgress : IPluginOperationProgress { public void Report(PluginProgress progress) { } }
    private sealed class LegacyRead(Dictionary<string, byte[]> files) : IArtifactReadService
    {
        public int Requests; public bool WrongDigest, Truncate; public Action? BeforeOpen;
        public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle content, CancellationToken token)
        {
            Requests++; token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ArtifactFileEntry>>(files.Select(p => new ArtifactFileEntry(p.Key, p.Value.LongLength,
                WrongDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray());
        }
        public ValueTask<Stream> OpenReadAsync(ArtifactContentHandle content, string path, CancellationToken token)
        {
            Requests++; BeforeOpen?.Invoke(); token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(Truncate ? files[path][..^1] : files[path], writable: false));
        }
    }
    private sealed class LegacyOutput : IArtifactTransformStaging, IRestoreMaterializationWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal); public int Allocations; public Action? BeforeWrite;
        public ValueTask<ArtifactStagingAllocation> CreateArtifactAsync(CancellationToken token)
        { Allocations++; return ValueTask.FromResult(new ArtifactStagingAllocation(new(Guid.NewGuid()), new("staging"))); }
        public ValueTask<Stream> OpenWriteAsync(ArtifactStagingHandle handle, string path, CancellationToken token) => OpenWriteAsync(path, token);
        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); BeforeWrite?.Invoke(); return ValueTask.FromResult<Stream>(new LegacyStoredOutput(bytes => Files.Add(path, bytes))); }
    }
    private sealed class LegacyStoredOutput(Action<byte[]> store) : MemoryStream
    {
        private bool _stored;
        protected override void Dispose(bool disposing) { if (disposing && !_stored) { _stored = true; store(ToArray()); } base.Dispose(disposing); }
    }
    private sealed class LegacyTemporaryStorage : IPluginTemporaryStorage, IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "legacy-full-" + Guid.NewGuid().ToString("N"))).FullName;
        public int Reservations, DirectoryRequests; public bool RefuseReservation;
        public ValueTask<string> CreateDirectoryAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); DirectoryRequests++; return ValueTask.FromResult(Root); }
        public ValueTask<IDisposable> ReserveAsync(string directory, long bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (RefuseReservation) throw new IOException("Insufficient capacity."); Reservations++; return ValueTask.FromResult<IDisposable>(new Release(() => Reservations--)); }
        public void Dispose() => Directory.Delete(Root, true);
        private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
