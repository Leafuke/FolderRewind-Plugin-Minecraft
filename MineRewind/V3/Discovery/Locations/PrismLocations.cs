namespace MineRewind.Discovery.Locations;

internal static class PrismLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming)) DiscoverRoot(Path.Combine(roaming, "PrismLauncher"), context, add);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(user)) DiscoverRoot(Path.Combine(user, "scoop", "persist", "prismlauncher"), context, add);
    }

    public static void DiscoverRoot(string root, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var config = Path.Combine(root, "prismlauncher.cfg");
        var instanceDir = context.ReadIni(config, "InstanceDir", "prism");
        var library = string.IsNullOrWhiteSpace(instanceDir) ? Path.Combine(root, "instances")
            : Path.IsPathFullyQualified(instanceDir) ? instanceDir : Path.Combine(root, instanceDir);
        if (Path.GetFileName(root).Equals("instances", StringComparison.OrdinalIgnoreCase)) library = root;
        ScanInstance(root);
        foreach (var instance in context.Directories(library, "prism")) ScanInstance(instance);
        // A selected custom InstanceDir may have any name.
        if (!Directory.Exists(library))
            foreach (var instance in context.Directories(root, "prism")) ScanInstance(instance);

        void ScanInstance(string instance)
        {
            context.Check();
            var instanceConfig = Path.Combine(instance, "instance.cfg");
            if (!File.Exists(instanceConfig)) return;
            var gameRoot = Path.Combine(instance, "minecraft");
            if (!Directory.Exists(gameRoot)) gameRoot = Path.Combine(instance, ".minecraft");
            var name = context.ReadIni(instanceConfig, "name", "prism") ?? Path.GetFileName(instance);
            add(new(gameRoot, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, name, "prism"));
        }
    }
}
