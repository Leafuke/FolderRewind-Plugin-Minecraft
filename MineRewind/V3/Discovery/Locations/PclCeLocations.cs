namespace MineRewind.Discovery.Locations;

internal static class PclCeLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(roaming)) return;
        DiscoverRoot(Path.Combine(roaming, "PCLCE"), context, add);
        ReadConfig(Path.Combine(roaming, ".PCLCE", "Config.json"), context, add);
    }

    public static void DiscoverRoot(string root, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        ReadConfig(Path.Combine(root, "config.v1.json"), context, add);
        ReadConfig(Path.Combine(root, "config.json"), context, add);
    }

    private static void ReadConfig(string path, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        using var json = context.ReadJson(path, "pclce");
        if (json?.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        PclWorkspaceLocations.ReadFolders(MinecraftDiscoveryContext.String(json.RootElement, "LaunchFolders"),
            "pclce", context, add);
        PclWorkspaceLocations.FromDownloadCache(MinecraftDiscoveryContext.String(json.RootElement, "CacheDownloadFolder"),
            "pclce", context, add);
    }
}
