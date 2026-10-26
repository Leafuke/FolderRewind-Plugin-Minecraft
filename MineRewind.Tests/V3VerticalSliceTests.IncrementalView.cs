using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task IncrementalViewReadsManyFilesWithExactPathsAndReleasesReaders()
    {
        var fixture = Activate();
        var (request, read, state) = IncrementalViewInput(fixture.Plugin, 2000);
        var view = await fixture.Plugin.OpenFileViewAsync(request, fixture.Invocation);
        CollectionAssert.AreEqual(state.Files.Select(f => f.Path).ToArray(), view.Files.Select(f => f.RelativePath).ToArray());
        foreach (var file in view.Files.Reverse())
        {
            await using var stream = await view.OpenReadAsync(file.RelativePath, CancellationToken.None);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(file.RelativePath), output.ToArray());
            Assert.AreEqual(file.Sha256, Convert.ToHexString(SHA256.HashData(output.ToArray())));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.OpenReadAsync("missing", CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => view.OpenReadAsync(state.Files[0].Path.ToLowerInvariant(), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => view.OpenReadAsync(state.Files[0].Path, new CancellationToken(true)).AsTask());
        await view.DisposeAsync();
        Assert.AreEqual(0, read.ActiveReaders);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => view.OpenReadAsync(state.Files[0].Path, CancellationToken.None).AsTask());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncrementalViewStillRejectsDuplicateAndCaseCollidingPaths(bool changeCase)
    {
        var fixture = Activate();
        var (request, read, state) = IncrementalViewInput(fixture.Plugin, 2);
        var first = state.Files[0];
        state = state with { Files = [first, first with { Path = changeCase ? first.Path.ToLowerInvariant() : first.Path }] };
        read.Files[(request.ArtifactsTopologicallySorted[^1].Content, WorldIncrementalFormat.RootPath)] = JsonSerializer.SerializeToUtf8Bytes(state, SemanticJson.Options);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Plugin.OpenFileViewAsync(request, fixture.Invocation).AsTask());
        Assert.AreEqual(0, read.ActiveReaders);
    }

    private static (ArtifactFileViewRequest Request, IncrementalViewRead Read, WorldIncrementalState State) IncrementalViewInput(
        MinecraftSavesPlugin plugin, int count)
    {
        var (config, folder) = Snapshots("synthetic-world");
        var dataId = new ArtifactId(Guid.NewGuid());
        var recipesId = new ArtifactId(Guid.NewGuid());
        var rootId = new ArtifactId(Guid.NewGuid());
        using var data = new MemoryStream();
        using var recipes = new MemoryStream();
        var files = new List<WorldStateFile>();
        for (var i = 0; i < count; i++)
        {
            var path = $"File-{i:D4}.bin";
            var bytes = Encoding.UTF8.GetBytes(path);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            var reference = new WorldObjectReference(dataId.Value, "pack.bin", data.Position, bytes.Length, sha);
            data.Write(bytes);
            var recipe = JsonSerializer.SerializeToUtf8Bytes(new WorldFileRecipe(1, "Raw", bytes.Length, [new(0, reference)]), SemanticJson.Options);
            files.Add(new(path, bytes.Length, sha, new(recipesId.Value, "pack.bin", recipes.Position, recipe.Length,
                Convert.ToHexString(SHA256.HashData(recipe))), null));
            recipes.Write(recipe);
        }
        var state = new WorldIncrementalState(1, folder.FolderId.ToString("D"), "boundary", files, [], [], true, data.Length, count);
        var read = new IncrementalViewRead();
        var dataNode = Node(dataId, "pack.bin", data.ToArray(), [], ArtifactCompleteness.Partial);
        var recipesNode = Node(recipesId, "pack.bin", recipes.ToArray(), [dataId], ArtifactCompleteness.Partial);
        var root = Node(rootId, WorldIncrementalFormat.RootPath, JsonSerializer.SerializeToUtf8Bytes(state, SemanticJson.Options), [recipesId], ArtifactCompleteness.Complete);
        return (new(config, folder, "version", rootId, [dataNode, recipesNode, root], read), read, state);

        BackupArtifactSnapshot Node(ArtifactId id, string path, byte[] bytes, ArtifactId[] dependencies, ArtifactCompleteness completeness)
        {
            var content = new ArtifactContentHandle(id.Value.ToString("D"));
            read.Files.Add((content, path), bytes);
            return new(id, new(new OwnerId(MinecraftSavesPlugin.PluginIdentity), WorldIncrementalFormat.Format), 1,
                plugin.RestoreStrategyId, content, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, completeness,
                CoreCaptureMode.Full, config.ConfigId, folder.FolderId, dependencies);
        }
    }

    private sealed class IncrementalViewRead : IArtifactReadService
    {
        public Dictionary<(ArtifactContentHandle Content, string Path), byte[]> Files { get; } = [];
        public int ActiveReaders { get; private set; }
        public ValueTask<IReadOnlyList<ArtifactFileEntry>> ListFilesAsync(ArtifactContentHandle content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<ArtifactFileEntry>>(Files.Where(p => p.Key.Content == content)
                .Select(p => new ArtifactFileEntry(p.Key.Path, p.Value.Length, Convert.ToHexString(SHA256.HashData(p.Value)))).ToArray());
        }
        public ValueTask<Stream> OpenReadAsync(ArtifactContentHandle content, string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ActiveReaders++;
            return ValueTask.FromResult<Stream>(new TrackedMemoryStream(Files[(content, relativePath)], () => ActiveReaders--));
        }
        private sealed class TrackedMemoryStream(byte[] bytes, Action release) : MemoryStream(bytes, false)
        {
            private bool _disposed;
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_disposed) { _disposed = true; release(); }
                base.Dispose(disposing);
            }
        }
    }
}
