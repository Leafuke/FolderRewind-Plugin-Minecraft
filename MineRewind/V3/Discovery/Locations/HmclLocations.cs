using System.Text.Json;

namespace MineRewind.Discovery.Locations;

internal static class HmclLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming)) ReadHome(Path.Combine(roaming, ".hmcl"), null, context, add);
        var home = MinecraftDiscoveryContext.Normalize(Environment.GetEnvironmentVariable("HMCL_USER_HOME"));
        if (home is not null) ReadHome(home, null, context, add);
        var local = MinecraftDiscoveryContext.Normalize(Environment.GetEnvironmentVariable("HMCL_LOCAL_HOME"));
        if (local is not null) ReadHome(local, null, context, add);
    }

    public static void DiscoverRoot(string root, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        ReadHome(Path.Combine(root, ".hmcl"), root, context, add);
        var isHome = Path.GetFileName(root).Equals(".hmcl", StringComparison.OrdinalIgnoreCase);
        if (isHome || File.Exists(Path.Combine(root, "config", "game-directories.json"))
            || File.Exists(Path.Combine(root, "config", "user-game-directories.json")))
            ReadHome(root, isHome ? Directory.GetParent(root)?.FullName : root, context, add);
        ReadConfig(Path.Combine(root, "hmcl.json"), root, context, add);
    }

    private static void ReadHome(string home, string? workspace, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        ReadConfig(Path.Combine(home, "config", "user-game-directories.json"), null, context, add);
        ReadConfig(Path.Combine(home, "config", "game-directories.json"), workspace, context, add);
        ReadConfig(Path.Combine(home, "hmcl.json"), workspace, context, add);
        ReadConfig(Path.Combine(home, "settings.json"), workspace, context, add);
    }

    private static void ReadConfig(string path, string? workspace, MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        using var json = context.ReadJson(path, "hmcl");
        if (json?.RootElement.ValueKind != JsonValueKind.Object) return;
        var root = json.RootElement;
        if (root.TryGetProperty("directories", out var directories) && directories.ValueKind == JsonValueKind.Array)
            foreach (var item in directories.EnumerateArray()) Emit(item, null);
        if (root.TryGetProperty("configurations", out var configurations) && configurations.ValueKind == JsonValueKind.Object)
            foreach (var item in configurations.EnumerateObject()) Emit(item.Value, item.Name);

        void Emit(JsonElement item, string? fallbackName)
        {
            context.Check();
            var raw = MinecraftDiscoveryContext.String(item, "path") ?? MinecraftDiscoveryContext.String(item, "gameDir");
            if (string.IsNullOrWhiteSpace(raw)) return;
            var resolved = MinecraftDiscoveryContext.Normalize(raw);
            if (resolved is null && workspace is not null && !Path.IsPathRooted(raw))
                resolved = MinecraftDiscoveryContext.Normalize(Path.Combine(workspace, raw));
            if (resolved is null) return;
            var name = MinecraftDiscoveryContext.String(item, "name") ?? fallbackName;
            if (name is null && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var labels)
                && labels.ValueKind == JsonValueKind.Object)
                name = MinecraftDiscoveryContext.String(labels, "default") ?? labels.EnumerateObject()
                    .Where(value => value.Value.ValueKind == JsonValueKind.String)
                    .Select(value => value.Value.GetString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            add(new(resolved, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, name, "hmcl"));
        }
    }
}
