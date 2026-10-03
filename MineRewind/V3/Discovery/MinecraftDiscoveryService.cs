using FolderRewind.Plugin.Abstractions;
using MineRewind.Discovery.Locations;

namespace MineRewind.Discovery;

internal sealed record MinecraftDiscoveryScan(IReadOnlyList<MinecraftDiscoveredInstance> Instances,
    IReadOnlyList<PluginDiagnostic> Diagnostics);

internal static class MinecraftDiscoveryService
{
    public static MinecraftDiscoveryScan Discover(DiscoveryRequest request, CancellationToken cancellationToken)
    {
        var context = new MinecraftDiscoveryContext(cancellationToken);
        var scanner = new MinecraftLayoutScanner(context);
        try
        {
            foreach (var raw in request.UserRoots ?? Array.Empty<string>())
            {
                context.Check();
                var root = MinecraftDiscoveryContext.Normalize(raw);
                if (root is null)
                {
                    context.Warn("minerewind.discovery_root_invalid", "user", raw ?? string.Empty);
                    continue;
                }
                if (!Directory.Exists(root)) continue;
                Run("user", root, () => scanner.ScanUserRoot(root));
                if (File.Exists(Path.Combine(root, "level.dat"))) continue;
                Run("hmcl", root, () => HmclLocations.DiscoverRoot(root, context, scanner.Scan));
                Run("pcl", root, () => PclWorkspaceLocations.DiscoverRoot(root, "pcl", context, scanner.Scan));
                Run("pclce", root, () => PclCeLocations.DiscoverRoot(root, context, scanner.Scan));
                Run("prism", root, () => PrismLocations.DiscoverRoot(root, context, scanner.Scan));
                Run("modrinth", root, () => ModrinthLocations.DiscoverRoot(root, context, scanner.Scan));
            }
            if (request.IncludeKnownLocations)
            {
                Run("official", string.Empty, () => KnownMinecraftLocations.Discover(context, scanner.Scan));
                Run("hmcl", string.Empty, () => HmclLocations.DiscoverKnown(context, scanner.Scan));
                Run("pcl2", string.Empty, () => PclLocations.DiscoverKnown(context, scanner.Scan));
                Run("pclce", string.Empty, () => PclCeLocations.DiscoverKnown(context, scanner.Scan));
                Run("prism", string.Empty, () => PrismLocations.DiscoverKnown(context, scanner.Scan));
                Run("modrinth", string.Empty, () => ModrinthLocations.DiscoverKnown(context, scanner.Scan));
                Run("netease", string.Empty, () => NeteaseLocations.DiscoverKnown(context, scanner.Scan));
            }
        }
        catch (MinecraftDiscoveryBudgetException ex)
        {
            context.Warn("minerewind.discovery_budget", ex.Reason, string.Empty);
        }
        return new(scanner.Instances, context.Diagnostics);

        void Run(string source, string path, Action action)
        {
            context.Check();
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { context.Warn("minerewind.discovery_source_failed", source, path); }
        }
    }
}
