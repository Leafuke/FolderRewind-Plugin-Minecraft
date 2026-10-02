namespace MineRewind.Discovery.Locations;

internal static class KnownMinecraftLocations
{
    public static void Discover(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(roaming))
        {
            add(new(Path.Combine(roaming, ".minecraft"), MinecraftLocationKind.JavaGameRoot,
                MinecraftEdition.Java, null, "official-java"));
            foreach (var user in context.Directories(Path.Combine(roaming, "Minecraft Bedrock", "Users"), "bedrock"))
                add(new(Path.Combine(user, "games", "com.mojang", "minecraftWorlds"),
                    MinecraftLocationKind.WorldsDirectory, MinecraftEdition.Bedrock,
                    "Bedrock · " + Path.GetFileName(user), "bedrock"));
        }
        if (!string.IsNullOrEmpty(local))
            add(new(Path.Combine(local, "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe", "LocalState",
                    "games", "com.mojang", "minecraftWorlds"), MinecraftLocationKind.WorldsDirectory,
                MinecraftEdition.Bedrock, "Bedrock", "bedrock"));
    }
}
