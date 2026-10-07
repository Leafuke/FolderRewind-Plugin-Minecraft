using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : IBackupArtifactTransformerCapability, IRestoreMaterializerCapability
{
    public ArtifactTransformerId TransformerId { get; } = new(MineRewindPluginId, "minecraft-world-full");
    public RestoreStrategyId RestoreStrategyId { get; } = new(MineRewindPluginId, "minecraft-world-materializer");
    private const long FullQuota = 16L * 1024 * 1024 * 1024;

    public async ValueTask<ArtifactTransformResult> TransformAsync(ArtifactTransformRequest request, PluginInvocationContext context)
    {
        if (request.Config.Kind != MinecraftKind || request.Primary.CoreCaptureMode != CoreCaptureMode.Full
            || request.Primary.Completeness != ArtifactCompleteness.Complete)
            throw new InvalidOperationException("Minecraft Full representation requires Java Full/Complete capture.");
        var token = context.OperationCancellation;
        var parent = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(token);
        var root = Path.Combine(parent, "full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
            var source = new DirectorySemanticScratch(Path.Combine(root, "source"), true);
            await CopyInputAsync(request.ArtifactRead, request.Primary.Content, source, token);
            if (!Directory.EnumerateFiles(source.Root, "level.dat", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("No Java world exists in the captured boundary.");
            var scratch = new DirectorySemanticScratch(Path.Combine(root, "scratch"), true);
            request.Progress.Report(new(0.2, "capture", "Encoding lossless world representation"));
            var captured = WorldCapture.Capture(new DirectoryWorldSource(source.Root), scratch, request.Folder.FolderId.ToString("D"), token);
            var files = WorldFullFormat.PrepareExport(scratch, token);
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
                    PluginIdentity, new Dictionary<string, string> { ["opaqueRegions"] = captured.Summary.OpaqueRegions.ToString() }) };
            return new(diagnostics.Length == 0 ? OperationOutcome.Success : OperationOutcome.SuccessWithWarnings,
                new(request.ExpectedGraphRevision,
                    [new(allocation.ArtifactId, allocation.Staging, new(MineRewindOwnerId, "minecraft-world-full"), 1,
                        RestoreStrategyId, ArtifactCompleteness.Complete, CoreCaptureMode.Full, [])], allocation.ArtifactId), diagnostics);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public async ValueTask<RestoreMaterializationResult> MaterializeAsync(RestoreMaterializationRequest request, PluginInvocationContext context)
    {
        var token = context.OperationCancellation;
        var artifact = request.ArtifactsTopologicallySorted.Single(a => a.ArtifactId == request.RootArtifactId);
        if (artifact.Format != new ArtifactFormatRef(MineRewindOwnerId, "minecraft-world-full") || artifact.FormatVersion != 1
            || artifact.Dependencies.Count != 0) throw new InvalidDataException("Unsupported Minecraft Full artifact.");
        var parent = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(token);
        var root = Path.Combine(parent, "materialize-" + Guid.NewGuid().ToString("N"));
        var input = new DirectorySemanticScratch(root, true);
        try
        {
            await CopyInputAsync(request.ArtifactRead, artifact.Content, input, token);
            WorldFullFormat.Materialize(input, new MaterializationSink(request.Workspace, token), request.Folder.FolderId.ToString("D"), token);
            return new(OperationOutcome.Success, []);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CopyInputAsync(IArtifactReadService read, ArtifactContentHandle handle, ISemanticScratch destination, CancellationToken token)
    {
        var files = await read.ListFilesAsync(handle, token);
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
