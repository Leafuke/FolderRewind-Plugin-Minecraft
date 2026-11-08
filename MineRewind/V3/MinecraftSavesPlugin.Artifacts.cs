using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : IBackupArtifactTransformerCapability, IRestoreMaterializerCapability, IArtifactFileViewProvider
{
    public ArtifactTransformerId TransformerId { get; } = new(MineRewindPluginId, "minecraft-world-full");
    public RestoreStrategyId RestoreStrategyId { get; } = new(MineRewindPluginId, "minecraft-world-materializer");
    public ValueTask<IArtifactFileView> OpenFileViewAsync(ArtifactFileViewRequest request, PluginInvocationContext context)
        => MinecraftIncrementalArtifactService.OpenFileViewAsync(request, context);
    private const long FullQuota = 16L * 1024 * 1024 * 1024;

    public async ValueTask<ArtifactTransformResult> TransformAsync(ArtifactTransformRequest request, PluginInvocationContext context)
    {
        if (request.IncrementalInput is not null)
            return await MinecraftIncrementalArtifactService.TransformAsync(request, context, RestoreStrategyId);
        // The transformer identity is shared with incremental-v1 and remains stable. Only the
        // legacy write protocol is retired; its materializer below continues to read v1 bytes.
        return new(OperationOutcome.Blocked, null,
            [Diagnostic("minerewind.incremental_input_required", DiagnosticSeverity.Error, "ArtifactTransform",
                ("en-US", "Legacy Full creation is retired. Semantic backups require incremental capture input."),
                ("zh-CN", "旧 Full 格式已停止生成，语义备份需要增量捕获上下文。"))]);
    }

    public async ValueTask<RestoreMaterializationResult> MaterializeAsync(RestoreMaterializationRequest request, PluginInvocationContext context)
    {
        var token = context.OperationCancellation;
        var artifact = request.ArtifactsTopologicallySorted.Single(a => a.ArtifactId == request.RootArtifactId);
        if (artifact.Format.FormatId == WorldIncrementalFormat.Format)
        {
            await using var view = await OpenFileViewAsync(new(request.Config, request.Folder, request.VersionId,
                request.RootArtifactId, request.ArtifactsTopologicallySorted, request.ArtifactRead), context);
            long materializedBytes = 0;
            foreach (var file in view.Files)
            {
                materializedBytes = checked(materializedBytes + file.Length);
                if (materializedBytes > request.MaximumOutputBytes) throw new InvalidDataException("Materialization byte budget exceeded.");
                await using var source = await view.OpenReadAsync(file.RelativePath, token);
                await using var output = await request.Workspace.OpenWriteAsync(file.RelativePath, token);
                await source.CopyToAsync(output, token);
            }
            return new(OperationOutcome.Success, []);
        }
        if (artifact.Format != new ArtifactFormatRef(MineRewindOwnerId, "minecraft-world-full") || artifact.FormatVersion != 1
            || artifact.Dependencies.Count != 0) throw new InvalidDataException("Unsupported Minecraft Full artifact.");
        var parent = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(token);
        var root = Path.Combine(parent, "materialize-" + Guid.NewGuid().ToString("N"));
        var input = new DirectorySemanticScratch(root, true);
        Exception? failure = null;
        try
        {
            var inventory = await request.ArtifactRead.ListFilesAsync(artifact.Content, token);
            if (inventory.Count > 100_000 || inventory.Any(file => file.Length < 0) || inventory.Sum(file => file.Length) > FullQuota)
                throw new IOException("Artifact input quota exceeded.");
            var bytes = checked(inventory.Sum(file => file.Length) + inventory.Count * 4096L);
            using (await context.HostServices.TemporaryStorage.ReserveAsync(root, bytes, token))
                await CopyInputAsync(request.ArtifactRead, artifact.Content, input, token, inventory);
            WorldFullFormat.Materialize(input, new MaterializationSink(request.Workspace, token), request.Folder.FolderId.ToString("D"), token,
                new SemanticArchiveReadLimits(request.MaximumOutputBytes));
            return new(OperationOutcome.Success, []);
        }
        catch (Exception error) { failure = error; throw; }
        finally { DeleteTemporaryArtifactWork(root, failure); }
    }

    private static void DeleteTemporaryArtifactWork(string root, Exception? failure)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (Exception cleanup) when (failure is not null) { failure.Data["TemporaryArtifactCleanupError"] = cleanup; }
    }

    private static async Task CopyInputAsync(IArtifactReadService read, ArtifactContentHandle handle, DirectorySemanticScratch destination, CancellationToken token,
        IReadOnlyList<ArtifactFileEntry>? inventory = null)
    {
        var files = inventory ?? await read.ListFilesAsync(handle, token);
        if (files.Count > 100_000 || files.Any(f => f.Length < 0) || files.Sum(f => f.Length) > FullQuota)
            throw new IOException("Artifact input quota exceeded.");
        long total = 0; var buffer = new byte[65536];
        foreach (var file in files)
        {
            await using var input = await read.OpenReadAsync(handle, file.RelativePath, token);
            using var output = destination.Create(file.RelativePath);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            long length = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total += count; length += count;
                if (total > FullQuota || length > file.Length) throw new IOException("Artifact input exceeded its declared size.");
                hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (length != file.Length || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256))
                throw new InvalidDataException("Artifact input hash mismatch.");
        }
    }

    private sealed class MaterializationSink(IRestoreMaterializationWorkspace workspace, CancellationToken token) : ISemanticScratch
    {
        public Stream Create(string path) => workspace.OpenWriteAsync(SemanticPaths.Normalize(path), token).AsTask().GetAwaiter().GetResult();
        public Stream OpenRead(string path) => throw new NotSupportedException();
        public bool Exists(string path) => throw new NotSupportedException();
    }
}
