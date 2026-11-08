using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task FullTransformUsesHostStreamsWithoutAnInputCopyAndRoundtrips()
    {
        using var temporary = new FullTestTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var data = new Dictionary<string, byte[]> { ["level.dat"] = [1, 2, 3], ["other.bin"] = new byte[2 * 1024 * 1024] };
        var read = new FullTestRead(data, temporary.Root); var staging = new FullTestStaging { BeforeAllocation = () => Assert.AreEqual(0, temporary.Reservations) };
        var primary = FullPrimary(config, folder);
        var result = await fixture.Plugin.TransformAsync(new(config, folder, "version", new("revision"), primary, [],
            new Dictionary<string, JsonElement>(), read, staging, new FullTestProgress()), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.IsFalse(read.ObservedInputCopy);
        Assert.AreEqual(data.Values.Sum(value => value.LongLength), read.BytesRead, "Protected input files are read once during capture.");
        Assert.AreEqual(0, temporary.Reservations);
        Assert.IsFalse(Directory.EnumerateDirectories(temporary.Root).Any());
        var node = result.Patch!.AddedArtifacts.Single();
        var artifact = primary with { ArtifactId = node.ArtifactId, Format = node.Format, FormatVersion = node.FormatVersion,
            RestoreStrategyId = node.RestoreStrategyId, Content = new("output") };
        var restored = new FullTestStaging();
        var materialized = await fixture.Plugin.MaterializeAsync(new(config, folder, "version", node.ArtifactId, [artifact],
            RestoreMode.Clean, RestoreMode.Clean, new FullTestRead(staging.Files, temporary.Root), restored,
            new FullTestProgress()), fixture.Invocation);
        Assert.AreEqual(OperationOutcome.Success, materialized.Outcome);
        CollectionAssert.AreEquivalent(data.Keys.ToArray(), restored.Files.Keys.ToArray());
        foreach (var pair in data) CollectionAssert.AreEqual(pair.Value, restored.Files[pair.Key]);
        Assert.AreEqual(0, temporary.Reservations);
    }

    [TestMethod]
    public async Task FullTransformRejectsWrongHostDigestBeforeStagingPublication()
    {
        using var temporary = new FullTestTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var read = new FullTestRead(new() { ["level.dat"] = [1, 2, 3] }, temporary.Root) { WrongDigest = true };
        var staging = new FullTestStaging();
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Plugin.TransformAsync(new(config, folder, "version", new("revision"),
            FullPrimary(config, folder), [], new Dictionary<string, JsonElement>(), read, staging, new FullTestProgress()), fixture.Invocation).AsTask());
        Assert.AreEqual(0, staging.Allocations);
        Assert.IsFalse(Directory.EnumerateDirectories(temporary.Root).Any());
        Assert.AreEqual(0, temporary.Reservations);
    }

    [TestMethod]
    [DataRow("reserve")] [DataRow("write")] [DataRow("cancel")]
    public async Task FullTransformFailuresReleaseSpaceAndOwnedFiles(string failure)
    {
        using var temporary = new FullTestTemporaryStorage { RefuseReservation = failure == "reserve" };
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        using var cancel = new CancellationTokenSource();
        var read = new FullTestRead(new() { ["level.dat"] = new byte[65536] }, temporary.Root)
            { BeforeOpen = failure == "cancel" ? cancel.Cancel : null };
        var staging = new FullTestStaging { BeforeAllocation = () =>
            { Assert.AreEqual(0, temporary.Reservations); if (failure == "write") throw new IOException("Staging allocation failed."); } };
        var invocation = fixture.Invocation with { OperationCancellation = cancel.Token };
        try
        {
            await fixture.Plugin.TransformAsync(new(config, folder, "version", new("revision"), FullPrimary(config, folder), [],
                new Dictionary<string, JsonElement>(), read, staging, new FullTestProgress()), invocation);
            Assert.Fail("Expected failure.");
        }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        Assert.AreEqual(0, temporary.Reservations);
        Assert.IsFalse(Directory.EnumerateDirectories(temporary.Root).Any());
        if (failure == "reserve") Assert.AreEqual(0L, read.BytesRead);
    }

    [TestMethod]
    public async Task ScratchCleanupFailurePreservesTheOriginalStagingFailureAndReleasesReservation()
    {
        using var temporary = new FullTestTemporaryStorage();
        var fixture = Activate(new FakeHostServices { TemporaryStorage = temporary });
        var (config, folder) = Snapshots("unused-live-world");
        var read = new FullTestRead(new() { ["level.dat"] = [1, 2, 3] }, temporary.Root);
        Stream? held = null;
        var staging = new FullTestStaging { BeforeAllocation = () =>
        {
            held = File.OpenRead(Directory.EnumerateFiles(temporary.Root, "raw.bin", SearchOption.AllDirectories).Single());
            throw new IOException("original staging failure");
        } };
        try
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Plugin.TransformAsync(new(config, folder,
                "version", new("revision"), FullPrimary(config, folder), [], new Dictionary<string, JsonElement>(), read,
                staging, new FullTestProgress()), fixture.Invocation).AsTask());
            Assert.AreEqual("original staging failure", error.Message);
            Assert.IsInstanceOfType<IOException>(error.Data["TemporaryArtifactCleanupError"]);
            Assert.AreEqual(0, temporary.Reservations);
        }
        finally { held?.Dispose(); }
    }

    [TestMethod]
    public void ScratchQuotaRejectsTheNextWriteBeforeAllocatingBytes()
    {
        using var temporary = new FullTestTemporaryStorage();
        var root = Path.Combine(temporary.Root, "scratch");
        var bounded = new BudgetedSemanticScratch(new MineRewind.World.Semantic.DirectorySemanticScratch(root, true), 4);
        using var output = bounded.Create("raw");
        output.Write(new byte[4]);
        Assert.ThrowsExactly<IOException>(() => output.WriteByte(5));
        Assert.AreEqual(4L, output.Length);
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

    private static BackupArtifactSnapshot FullPrimary(ConfigSnapshot config, FolderSnapshot folder) => new(new(Guid.NewGuid()),
        new(new("folderrewind.core"), "archive-set"), 1, new(new("folderrewind.core"), "archive-materializer"), new("primary"),
        "unused", 0, ArtifactCompleteness.Complete, CoreCaptureMode.Full, config.ConfigId, folder.FolderId, []);
    private sealed class FullTestProgress : IPluginOperationProgress { public void Report(PluginProgress progress) { } }
    private sealed class FullTestRead(Dictionary<string, byte[]> files, string temporary) : IArtifactReadService
    {
        public long BytesRead; public bool ObservedInputCopy; public bool WrongDigest; public Action? BeforeOpen;
        public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle content, CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<ArtifactFileEntry>>(files.Select(p => new ArtifactFileEntry(p.Key, p.Value.LongLength,
                WrongDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray());
        public ValueTask<Stream> OpenReadAsync(ArtifactContentHandle content, string path, CancellationToken token)
        {
            BeforeOpen?.Invoke();
            token.ThrowIfCancellationRequested();
            ObservedInputCopy |= Directory.EnumerateDirectories(temporary, "source", SearchOption.AllDirectories).Any();
            return ValueTask.FromResult<Stream>(new CountingInput(files[path], count => BytesRead += count));
        }
    }
    private sealed class CountingInput(byte[] bytes, Action<int> read) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) { var result = base.Read(buffer, offset, count); read(result); return result; }
        public override int Read(Span<byte> buffer) { var result = base.Read(buffer); read(result); return result; }
    }
    private sealed class FullTestStaging : IArtifactTransformStaging, IRestoreMaterializationWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal); public int Allocations; public Action? BeforeAllocation;
        public ValueTask<ArtifactStagingAllocation> CreateArtifactAsync(CancellationToken token)
        { BeforeAllocation?.Invoke(); Allocations++; return ValueTask.FromResult(new ArtifactStagingAllocation(new(Guid.NewGuid()), new("staging"))); }
        public ValueTask<Stream> OpenWriteAsync(ArtifactStagingHandle handle, string path, CancellationToken token) => OpenWriteAsync(path, token);
        public ValueTask<Stream> OpenWriteAsync(string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new StoredOutput(bytes => Files.Add(path, bytes))); }
    }
    private sealed class StoredOutput(Action<byte[]> store) : MemoryStream
    {
        private bool _stored;
        protected override void Dispose(bool disposing) { if (disposing && !_stored) { _stored = true; store(ToArray()); } base.Dispose(disposing); }
    }
    private sealed class FullTestTemporaryStorage : IPluginTemporaryStorage, IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "full-transform-" + Guid.NewGuid().ToString("N"))).FullName;
        public int Reservations;
        public bool RefuseReservation;
        public ValueTask<string> CreateDirectoryAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Root); }
        public ValueTask<IDisposable> ReserveAsync(string directory, long bytes, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (RefuseReservation) throw new IOException("Insufficient capacity."); Reservations++; return ValueTask.FromResult<IDisposable>(new Release(() => Reservations--)); }
        public void Dispose() => Directory.Delete(Root, true);
        private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
