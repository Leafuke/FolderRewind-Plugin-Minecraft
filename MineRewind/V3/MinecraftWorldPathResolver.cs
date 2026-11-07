namespace MineRewind;

internal static class MinecraftWorldPathResolver
{
    internal static World.WorldObservation Observe(string path, CancellationToken token = default)
        => World.WorldObserver.Observe(path, token);
    internal static string? TryResolveWorldPath(string? configuredRootPath)
        => World.WorldLocator.TryResolveWorldPath(configuredRootPath);
}
