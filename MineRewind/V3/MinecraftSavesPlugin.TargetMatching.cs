using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : ICorrelatedTargetMatcher
{
    public async ValueTask<CorrelatedTargetMatches> MatchAsync(string physicalRoot, PluginInvocationContext context)
    {
        EnsureActivated();
        var configs = await context.HostServices.Configs.QueryAsync(MinecraftKind, context.OperationCancellation)
            .ConfigureAwait(false);
        return await Task.Run(() => MatchPhysicalWorld(physicalRoot, configs, context.OperationCancellation),
            context.OperationCancellation).ConfigureAwait(false);
    }

    private static CorrelatedTargetMatches MatchPhysicalWorld(string requestedPath, IReadOnlyList<ConfigSnapshot> configs,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestedRoot = PhysicalWorldDirectory.Resolve(requestedPath);
            var targets = new List<CorrelatedTargetMatch>();
            foreach (var config in configs.Where(config => config.Kind == MinecraftKind))
            foreach (var folder in config.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observed = MinecraftWorldPathResolver.Observe(folder.Path, cancellationToken);
                if (!observed.IsComplete) return new(false, []);
                foreach (var world in observed.Worlds)
                {
                    var root = PhysicalWorldDirectory.Resolve(world.Path);
                    if (!string.Equals(root, requestedRoot, StringComparison.Ordinal)) continue;
                    if (!world.HasPrimaryMetadata) return new(false, []);
                    var source = PhysicalWorldDirectory.Resolve(folder.Path);
                    targets.Add(new(config.ConfigId, folder.FolderId, root, !string.Equals(source, root, StringComparison.Ordinal)));
                }
            }
            return new(true, targets);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(false, []);
        }
    }
}
