namespace MineRewind.Discovery.Locations;

internal static class ModrinthLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming))
            foreach (var name in new[] { "ModrinthApp", "com.modrinth.theseus" })
                DiscoverRoot(Path.Combine(roaming, name), context, add);
        var configured = MinecraftDiscoveryContext.Normalize(Environment.GetEnvironmentVariable("THESEUS_CONFIG_DIR"));
        if (configured is not null) DiscoverRoot(configured, context, add);
    }

    public static void DiscoverRoot(string root, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var profiles = Path.GetFileName(root).Equals("profiles", StringComparison.OrdinalIgnoreCase)
            ? root : Path.Combine(root, "profiles");
        foreach (var profile in context.Directories(profiles, "modrinth"))
            add(new(profile, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, Path.GetFileName(profile), "modrinth"));
    }
}
