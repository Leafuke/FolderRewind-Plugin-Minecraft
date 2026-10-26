using System.Security.Cryptography;
using System.Buffers;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private async ValueTask<ArtifactTransformResult> TransformIncrementalAsync(ArtifactTransformRequest request, PluginInvocationContext context)
    {
        var capture = request.IncrementalInput!; var token = context.OperationCancellation;
        if (request.Config.Kind != MinecraftKind || capture.DetectionPolicy != "size-mtime-v1"
            || capture.CaptureId == Guid.Empty || string.IsNullOrWhiteSpace(capture.BoundaryFingerprint)
            || request.Primary.CoreCaptureMode == CoreCaptureMode.Rolling)
            throw new InvalidDataException("Unsupported incremental capture context.");
        var parent = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(token);
        var work = Path.Combine(parent, "incremental-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inventory = await request.ArtifactRead.ListFilesAsync(request.Primary.Content, token);
            var source = new ArtifactWorldSource(request.ArtifactRead, request.Primary.Content, inventory, token);
            using var reader = new ArtifactObjects(request.ReadableArtifacts, request.ArtifactRead, request.Config.ConfigId, request.Folder.FolderId);
            WorldIncrementalState? baseline = null;
            if (capture.BaseRootArtifactId is { } baseRoot)
                baseline = await LoadStateAsync(baseRoot, request.ReadableArtifacts, request.ArtifactRead, request.Folder.FolderId, token);
            if (capture.Kind == ArtifactCaptureInputKind.ChangesOnly && baseline is null)
                throw new InvalidDataException("Incremental capture has no fixed semantic baseline.");
            using var writer = new ArtifactPacks(request.Staging, RestoreStrategyId, request.Primary.CoreCaptureMode, work + "-packs");
            var state = WorldIncrementalFormat.Capture(source, baseline, reader, writer,
                request.Folder.FolderId.ToString("D"), capture.BoundaryFingerprint, capture.AddedFiles,
                capture.ModifiedFiles, capture.DeletedFiles, capture.Checkpoint, token);
            writer.Seal();
            var root = await request.Staging.CreateArtifactAsync(token);
            await using (var output = await request.Staging.OpenWriteAsync(root.Staging, WorldIncrementalFormat.RootPath, token))
                await JsonSerializer.SerializeAsync(output, state, SemanticJson.Options, token);
            var dependencies = state.Files.SelectMany(f => f.Index is null ? new[] { f.Recipe.ArtifactId } : new[] { f.Recipe.ArtifactId, f.Index.ArtifactId })
                .Distinct().Select(id => new ArtifactId(id)).ToArray();
            var nodes = writer.ReachableNodes(dependencies).Append(new(root.ArtifactId, root.Staging, IncrementalFormat, 1, RestoreStrategyId,
                ArtifactCompleteness.Complete, request.Primary.CoreCaptureMode, dependencies)).ToArray();
            // Packs are batch-owned; no per-chunk Artifact and no previous-state-root dependency.
            return new(OperationOutcome.Success, new(request.ExpectedGraphRevision, nodes, root.ArtifactId), [])
            { RequiresFullVerification = state.Checkpoint };
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    private static ArtifactFormatRef IncrementalFormat => new(MineRewindOwnerId, WorldIncrementalFormat.Format);

    // The Host owns this stable, bounded view for the invocation. No live-world access
    // and no second scratch copy; the domain codec only receives declared input files.
    private sealed class ArtifactWorldSource(IArtifactReadService read, ArtifactContentHandle content,
        IReadOnlyList<ArtifactFileEntry> entries, CancellationToken operationToken) : IReadOnlyWorldSource
    {
        private readonly IReadOnlyList<SourceFile> _files = entries.Select(f =>
            new SourceFile(SemanticPaths.Normalize(f.RelativePath), f.Length, DateTime.UnixEpoch) { Sha256 = f.Sha256 }).ToArray();
        private readonly HashSet<string> _paths = entries.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return _files; }
        public Stream OpenRead(string path)
        {
            operationToken.ThrowIfCancellationRequested();
            if (!_paths.Contains(path)) throw new FileNotFoundException("File is outside the Host change view.", path);
            return read.OpenReadAsync(content, path, operationToken).AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask<IArtifactFileView> OpenFileViewAsync(ArtifactFileViewRequest request, PluginInvocationContext context)
    {
        var state = await LoadStateAsync(request.RootArtifactId, request.ArtifactsTopologicallySorted,
            request.ArtifactRead, request.Folder.FolderId, context.OperationCancellation);
        return new IncrementalView(state, new ArtifactObjects(request.ArtifactsTopologicallySorted, request.ArtifactRead,
            request.Config.ConfigId, request.Folder.FolderId));
    }

    private static async Task<WorldIncrementalState> LoadStateAsync(ArtifactId root,
        IReadOnlyList<BackupArtifactSnapshot> artifacts, IArtifactReadService read, Guid source, CancellationToken token)
    {
        var artifact = artifacts.Single(a => a.ArtifactId == root);
        if (artifact.Format != IncrementalFormat || artifact.FormatVersion != 1 || artifact.Completeness != ArtifactCompleteness.Complete)
            throw new InvalidDataException("Unsupported incremental state root.");
        var entries = await read.ListFilesAsync(artifact.Content, token);
        var entry = entries.Single(f => f.RelativePath == WorldIncrementalFormat.RootPath);
        if (entry.Length > 64L * 1024 * 1024) throw new InvalidDataException("State directory quota exceeded.");
        await using var input = await read.OpenReadAsync(artifact.Content, entry.RelativePath, token);
        var state = await JsonSerializer.DeserializeAsync<WorldIncrementalState>(input, SemanticJson.Options, token)
            ?? throw new InvalidDataException("Empty state root.");
        if (state.Version != 1 || state.SourceId != source.ToString("D") || state.Files.Count > SemanticPaths.MaximumInventoryEntries
            || state.Files.Any(f => f.Length < 0) || state.Files.Sum(f => f.Length) > FullQuota
            || state.Files.Select(f => SemanticPaths.Normalize(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
            throw new InvalidDataException("Invalid state directory.");
        var map = artifacts.ToDictionary(a => a.ArtifactId.Value);
        foreach (var file in state.Files)
            foreach (var reference in file.Index is null ? new[] { file.Recipe } : new[] { file.Recipe, file.Index })
                if (!map.ContainsKey(reference.ArtifactId) || !artifact.Dependencies.Contains(new(reference.ArtifactId)))
                    throw new InvalidDataException("State recipe is outside declared dependencies.");
        return state;
    }

    private sealed class IncrementalView(WorldIncrementalState state, ArtifactObjects objects) : IArtifactFileView
    {
        private bool _disposed;
        private readonly IReadOnlyDictionary<string, WorldStateFile> _files = state.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        public IReadOnlyList<ArtifactMaterializedFile> Files { get; } = Array.AsReadOnly(state.Files.Select(f => new ArtifactMaterializedFile(
            f.Path, f.Length, f.Sha256, new(new(f.Recipe.ArtifactId), f.Recipe.Path, f.Recipe.Offset, f.Recipe.Length,
                f.Recipe.Sha256, WorldIncrementalFormat.Format, 1))).ToArray());
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_files.TryGetValue(path, out var file))
                throw new InvalidOperationException("Sequence contains no matching element");
            return ValueTask.FromResult(WorldIncrementalFormat.OpenFile(file, objects, token));
        }
        public ValueTask DisposeAsync() { _disposed = true; objects.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ArtifactObjects : IWorldObjectReader, IDisposable
    {
        private readonly Dictionary<Guid, BackupArtifactSnapshot> _artifacts;
        private readonly IArtifactReadService _read;
        private readonly object _poolLock = new();
        private readonly Dictionary<Guid, LinkedListNode<(Guid Id, Stream Stream)>> _idle = [];
        private readonly LinkedList<(Guid Id, Stream Stream)> _lru = new();
        private bool _disposed;
        public ArtifactObjects(IReadOnlyList<BackupArtifactSnapshot> artifacts, IArtifactReadService read, string config, Guid source)
        {
            if (artifacts.Any(a => a.ConfigId != config || a.FolderId != source || a.Format != IncrementalFormat || a.FormatVersion != 1))
                throw new InvalidDataException("Foreign or unsupported Artifact in source closure.");
            _artifacts = artifacts.ToDictionary(a => a.ArtifactId.Value); _read = read;
        }
        public Stream Open(WorldObjectReference reference, CancellationToken token)
            => OpenObject(reference, token, sequential: false);
        public Stream OpenSequential(WorldObjectReference reference, CancellationToken token)
            => OpenObject(reference, token, sequential: true);
        private Stream OpenObject(WorldObjectReference reference, CancellationToken token, bool sequential)
        {
            using var stage = SemanticDiagnostics.Source.StartActivity(sequential ? "object.stream.open" : "object.preverify");
            stage?.SetTag("object.bytes", reference.Length);
            if (!_artifacts.TryGetValue(reference.ArtifactId, out var artifact) || reference.Path != "pack.bin"
                || reference.Offset < 0 || reference.Length < 0 || reference.Length > WorldIncrementalFormat.DataPackBytes)
                throw new InvalidDataException("Invalid object reference.");
            Stream? input = null;
            lock (_poolLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_idle.Remove(reference.ArtifactId, out var idle)) { input = idle.Value.Stream; _lru.Remove(idle); }
            }
            input ??= _read.OpenReadAsync(artifact.Content, reference.Path, token).AsTask().GetAwaiter().GetResult();
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(65536, Math.Max(1, reference.Length)));
            try
            {
                if (!input.CanSeek || reference.Offset > input.Length || reference.Length > input.Length - reference.Offset)
                    throw new InvalidDataException("Object reference is outside its pack.");
                input.Position = reference.Offset;
                if (sequential)
                    return new VerifiedObjectStream(new ObjectSlice(input, reference.Offset, reference.Length,
                        stream => Return(reference.ArtifactId, stream)), reference.Length, reference.Sha256, token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var left = reference.Length;
                while (left > 0) { token.ThrowIfCancellationRequested(); var n = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length)); if (n == 0) throw new EndOfStreamException(); hash.AppendData(buffer, 0, n); left -= n; }
                if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), reference.Sha256))
                    throw new InvalidDataException("Object reference digest mismatch.");
                input.Position = reference.Offset;
                return new ObjectSlice(input, reference.Offset, reference.Length, stream => Return(reference.ArtifactId, stream));
            }
            catch { input.Dispose(); throw; }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        private void Return(Guid id, Stream stream)
        {
            lock (_poolLock)
            {
                if (_disposed || _idle.ContainsKey(id)) { stream.Dispose(); return; }
                if (_idle.Count >= 32)
                {
                    var oldest = _lru.First!;
                    _idle.Remove(oldest.Value.Id); _lru.RemoveFirst(); oldest.Value.Stream.Dispose();
                }
                _idle.Add(id, _lru.AddLast((id, stream)));
            }
        }
        public void Dispose()
        {
            lock (_poolLock)
            {
                _disposed = true;
                foreach (var node in _idle.Values) node.Value.Stream.Dispose();
                _idle.Clear(); _lru.Clear();
            }
        }
        public void ValidateDependency(Guid owner, Guid dependency)
        {
            if (!_artifacts.TryGetValue(owner, out var node) || !node.Dependencies.Contains(new(dependency)))
                throw new InvalidDataException("Recipe references an undeclared Artifact dependency.");
        }
        public long GetPackLength(Guid id) => _artifacts.TryGetValue(id, out var node) ? node.LogicalSize
            : throw new InvalidDataException("Pack is outside the closure.");
        public IReadOnlyCollection<Guid> GetDependencies(Guid id) => _artifacts[id].Dependencies.Select(d => d.Value).ToArray();
    }

    private sealed class ArtifactPacks(IArtifactTransformStaging staging, RestoreStrategyId strategy, CoreCaptureMode mode, string spool) : IWorldObjectWriter, IDisposable
    {
        // The v1 object bound stays 64 MiB; new region packs target 16 MiB to limit
        // whole-envelope decoding for sparse reads. Keep a fitting file together.
        private const long DataTargetBytes = 16L * 1024 * 1024;
        private sealed class Pack(ArtifactStagingAllocation allocation, Stream stream)
        { public ArtifactStagingAllocation Allocation = allocation; public Stream Stream = stream; public long Length; public HashSet<ArtifactId> Dependencies = []; }
        private Pack? _data, _metadata;
        private readonly HashSet<Guid> _owned = [];
        private readonly Dictionary<Guid, long> _lengths = [];
        private readonly Dictionary<(Guid, long), (string Path, WorldObjectReference Reference)> _metadataFiles = [];
        public List<StagedArtifactNode> Nodes { get; } = [];
        public bool Owns(Guid artifactId) => _owned.Contains(artifactId);
        public long GetPackLength(Guid id) => _lengths[id];
        public IReadOnlyCollection<Guid> GetDependencies(Guid id)
        {
            if (_data?.Allocation.ArtifactId.Value == id) return [];
            return (_metadata?.Allocation.ArtifactId.Value == id ? _metadata.Dependencies.AsEnumerable()
                : Nodes.Single(n => n.ArtifactId.Value == id).Dependencies).Select(d => d.Value).ToArray();
        }
        public void BeginRepack() => Finish(ref _metadata);
        public void BeginFile(long length)
        {
            if (_data is { Length: > 0 } && _data.Length + Math.Min(length, DataTargetBytes) > DataTargetBytes)
                Finish(ref _data);
        }
        public Stream Open(WorldObjectReference reference, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_metadataFiles.TryGetValue((reference.ArtifactId, reference.Offset), out var entry) || entry.Reference != reference)
                throw new InvalidDataException("Only this capture's exact metadata objects can be read during repack.");
            return File.OpenRead(entry.Path);
        }
        public void ValidateDependency(Guid owner, Guid dependency)
        {
            IEnumerable<ArtifactId> dependencies = _metadata?.Allocation.ArtifactId.Value == owner ? _metadata.Dependencies
                : Nodes.Single(n => n.ArtifactId.Value == owner).Dependencies;
            if (!dependencies.Contains(new ArtifactId(dependency))) throw new InvalidDataException("Undeclared staging dependency.");
        }
        public IEnumerable<StagedArtifactNode> ReachableNodes(IEnumerable<ArtifactId> roots)
        {
            var nodes = Nodes.ToDictionary(n => n.ArtifactId);
            var visited = new HashSet<ArtifactId>(); var pending = new Stack<ArtifactId>(roots);
            while (pending.TryPop(out var id))
                if (visited.Add(id) && nodes.TryGetValue(id, out var node))
                    foreach (var dependency in node.Dependencies) pending.Push(dependency);
            return Nodes.Where(n => visited.Contains(n.ArtifactId));
        }
        public WorldObjectReference Store(Stream input, long length, bool metadata, IReadOnlyCollection<Guid> dependencies, CancellationToken token)
        {
            var limit = metadata ? WorldIncrementalFormat.RecipePackBytes : WorldIncrementalFormat.DataPackBytes;
            if (length < 0 || length > limit) throw new IOException("Object exceeds its pack budget.");
            ref var pack = ref (metadata ? ref _metadata : ref _data);
            var target = metadata ? limit : Math.Max(DataTargetBytes, length);
            if (pack is null || pack.Length + length > target)
            {
                Finish(ref pack);
                var allocation = staging.CreateArtifactAsync(token).AsTask().GetAwaiter().GetResult();
                _owned.Add(allocation.ArtifactId.Value);
                pack = new(allocation, staging.OpenWriteAsync(allocation.Staging, "pack.bin", token).AsTask().GetAwaiter().GetResult());
            }
            var offset = pack.Length;
            string? metadataPath = null;
            if (metadata) { Directory.CreateDirectory(spool); metadataPath = Path.Combine(spool, Guid.NewGuid().ToString("N")); }
            using var mirror = metadataPath is null ? null : File.Create(metadataPath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(65536, Math.Max(1, length))); var left = length;
            try
            {
                while (left > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var count = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                    if (count == 0) throw new EndOfStreamException();
                    pack.Stream.Write(buffer, 0, count); mirror?.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); left -= count;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            var sha = Convert.ToHexString(hash.GetHashAndReset());
            pack.Length += length;
            _lengths[pack.Allocation.ArtifactId.Value] = pack.Length;
            foreach (var id in dependencies) pack.Dependencies.Add(new(id));
            var reference = new WorldObjectReference(pack.Allocation.ArtifactId.Value, "pack.bin", offset, length, sha);
            if (metadataPath is not null) _metadataFiles.Add((reference.ArtifactId, offset), (metadataPath, reference));
            return reference;
        }
        private void Finish(ref Pack? pack)
        {
            if (pack is null) return;
            pack.Stream.Dispose();
            Nodes.Add(new(pack.Allocation.ArtifactId, pack.Allocation.Staging, IncrementalFormat, 1, strategy,
                ArtifactCompleteness.Partial, mode, pack.Dependencies.OrderBy(a => a.Value).ToArray()));
            pack = null;
        }
        public void Seal() { Finish(ref _data); Finish(ref _metadata); }
        public void Dispose()
        {
            _data?.Stream.Dispose(); _metadata?.Stream.Dispose();
            if (Directory.Exists(spool)) Directory.Delete(spool, true);
        }
    }

    private sealed class ObjectSlice(Stream input, long start, long length, Action<Stream>? release = null) : Stream
    {
        private bool _disposed;
        public override int Read(byte[] buffer, int offset, int count)
        { ObjectDisposedException.ThrowIf(_disposed, this); return input.Read(buffer, offset, (int)Math.Min(count, length - Position)); }
        public override int Read(Span<byte> buffer)
        { ObjectDisposedException.ThrowIf(_disposed, this); return input.Read(buffer[..(int)Math.Min(buffer.Length, length - Position)]); }
        public override bool CanRead => !_disposed; public override bool CanSeek => !_disposed; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get { ObjectDisposedException.ThrowIf(_disposed, this); return input.Position - start; } set { ObjectDisposedException.ThrowIf(_disposed, this); if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value)); input.Position = start + value; } }
        public override long Seek(long offset, SeekOrigin origin) { Position = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length)); return Position; }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed) { _disposed = true; if (release is null) input.Dispose(); else release(input); }
            base.Dispose(disposing);
        }
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
