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
        if (request.Config.Kind != MinecraftKind || request.Primary.CoreCaptureMode != CoreCaptureMode.Full
            || request.Primary.Completeness != ArtifactCompleteness.Complete)
            throw new InvalidOperationException("Minecraft Full representation requires Java Full/Complete capture.");
        var token = context.OperationCancellation;
        var parent = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(token);
        var root = Path.Combine(parent, "full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        try
        {
            var inventory = await request.ArtifactRead.ListFilesAsync(request.Primary.Content, token);
            var regionCount = inventory.Count(f => f.RelativePath.EndsWith(".mca", StringComparison.OrdinalIgnoreCase));
            // A region emits at most payload + residual objects. Non-region files emit one raw object.
            // Reserve conservative index/JSON overhead before doing the expensive lossless pass.
            var estimatedFiles = checked((long)inventory.Count + regionCount + 4);
            var estimatedBytes = checked(inventory.Sum(f => f.Length) + regionCount * 1024L * 1024 + inventory.Count * 4096L);
            if (inventory.Any(f => f.Length < 0) || estimatedFiles > 100_000 || estimatedBytes > FullQuota)
                throw new IOException("Estimated Full representation exceeds the staging budget.");
            var source = new ArtifactWorldSource(request.ArtifactRead, request.Primary.Content, inventory, token);
            if (!inventory.Any(f => f.RelativePath == "level.dat" || f.RelativePath.EndsWith("/level.dat", StringComparison.Ordinal)))
                throw new InvalidDataException("No Java world exists in the captured boundary.");
            var scratch = new BudgetedSemanticScratch(new DirectorySemanticScratch(Path.Combine(root, "scratch"), true), estimatedBytes);
            request.Progress.Report(new(0.2, "capture", "Encoding lossless world representation"));
            ScratchCompletion captured; IReadOnlyList<string> files;
            using (await context.HostServices.TemporaryStorage.ReserveAsync(root, estimatedBytes, token))
            {
                captured = WorldCapture.CaptureVerified(source, scratch, request.Folder.FolderId.ToString("D"), token);
                files = WorldFullFormat.PrepareExport(scratch, token);
            }
            // Scratch is now occupied disk space. Staging reserves its own outstanding writes.
            var allocation = await request.Staging.CreateArtifactAsync(token);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                using var input = scratch.OpenRead(file);
                await using var output = await request.Staging.OpenWriteAsync(allocation.Staging, file, token);
                await input.CopyToAsync(output, token);
            }
            request.Progress.Report(new(1, "sealed", "Lossless world representation prepared"));
            var diagnostics = captured.Summary.FullyComparable ? Array.Empty<PluginDiagnostic>() :
                new[] { new PluginDiagnostic("minerewind.full.semantic_coverage_limited", DiagnosticSeverity.Warning, "ArtifactTransform",
                    PluginIdentity, new Dictionary<string, string> { ["opaqueRegions"] = captured.Summary.OpaqueRegions.ToString(System.Globalization.CultureInfo.InvariantCulture) }) };
            return new(diagnostics.Length == 0 ? OperationOutcome.Success : OperationOutcome.SuccessWithWarnings,
                new(request.ExpectedGraphRevision,
                    [new(allocation.ArtifactId, allocation.Staging, new(MineRewindOwnerId, "minecraft-world-full"), 1,
                        RestoreStrategyId, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [])], allocation.ArtifactId), diagnostics);
        }
        catch (Exception error) { failure = error; throw; }
        finally { DeleteTemporaryArtifactWork(root, failure); }
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
