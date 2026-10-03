namespace MineRewind.Discovery.Locations;

internal static class PclWorkspaceLocations
{
    private static readonly string[] ExecutableNames =
    ["Plain Craft Launcher 2.exe", "Plain Craft Launcher Community Edition.exe",
        "PCL2_CE_Release_x64.exe", "PCL2_CE_Beta_x64.exe"];

    public static void ReadFolders(string? folders, string source, MinecraftDiscoveryContext context,
        Action<MinecraftLocation> add)
    {
        if (string.IsNullOrWhiteSpace(folders)) return;
        if (folders.Length > MinecraftDiscoveryContext.TextLimit)
        {
            context.Warn("minerewind.discovery_config_too_large", source, string.Empty);
            return;
        }
        foreach (var entry in folders.Split('|'))
        {
            context.Check();
            var split = entry.IndexOf('>');
            if (split < 0) continue;
            var root = MinecraftDiscoveryContext.Normalize(entry[(split + 1)..].Trim());
            if (root is not null)
                add(new(root, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java,
                    entry[..split].Trim(), source));
        }
    }

    public static void FromDownloadCache(string? download, string source, MinecraftDiscoveryContext context,
        Action<MinecraftLocation> add)
    {
        var path = MinecraftDiscoveryContext.Normalize(download);
        // A cache may be stale or customized. Only local launcher markers make it a useful anchor.
        for (var level = 0; path is not null && level <= 3; level++)
        {
            context.Check();
            DiscoverRoot(path, source, context, add);
            path = Directory.GetParent(path)?.FullName;
        }
    }

    public static void DiscoverRoot(string root, string source, MinecraftDiscoveryContext context,
        Action<MinecraftLocation> add)
    {
        context.Check();
        var nested = Path.Combine(root, "PCL");
        if (HasConfig(nested)) ScanWorkspace(root, nested);
        if (HasConfig(root))
        {
            // A PCL data directory can be selected directly. Its parent is a bounded workspace hint.
            var parent = Directory.GetParent(root)?.FullName;
            if (parent is not null) ScanWorkspace(parent, root);
            if (ExecutableNames.Any(name => File.Exists(Path.Combine(root, name)))) ScanWorkspace(root, root);
        }

        void ScanWorkspace(string workspace, string configRoot)
        {
            add(new(workspace, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, source));
            var selected = context.ReadIni(Path.Combine(configRoot, "Setup.ini"), "LaunchFolderSelect", source, ':');
            var selectedRoot = MinecraftDiscoveryContext.Normalize(selected);
            if (selectedRoot is null && selected?.StartsWith('$') == true)
                selectedRoot = MinecraftDiscoveryContext.Normalize(Path.Combine(workspace, selected[1..].TrimStart('\\', '/')));
            if (selectedRoot is not null)
                add(new(selectedRoot, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, source));
            foreach (var child in context.Directories(workspace, source))
                if (Path.GetFileName(child).Equals(".minecraft", StringComparison.OrdinalIgnoreCase)
                    || Directory.Exists(Path.Combine(child, "saves")) || Directory.Exists(Path.Combine(child, "versions")))
                    add(new(child, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, source));
        }
    }

    private static bool HasConfig(string path)
        => File.Exists(Path.Combine(path, "Setup.ini")) || File.Exists(Path.Combine(path, "config.v1.yml"));
}
