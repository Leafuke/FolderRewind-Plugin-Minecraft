namespace MineRewind;

internal static class MinecraftWorldPathResolver
{
    internal static string? TryResolveWorldPath(string? configuredRootPath)
        => World.WorldLocator.TryResolveWorldPath(configuredRootPath);
}