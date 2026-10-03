namespace MineRewind;

/// <summary>
/// 将受管源目录解析为真实世界目录；同时覆盖客户端世界与 dedicated server 的 level-name 语义。
/// </summary>
internal static class MinecraftWorldPathResolver
{
    internal static string? TryResolveWorldPath(string? configuredRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredRootPath) || !Directory.Exists(configuredRootPath)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRootPath));
            if (HasLevelDat(root)) return root;

            var properties = Path.Combine(root, "server.properties");
            if (File.Exists(properties))
            {
                if (!TryReadLevelName(properties, out var levelName)) return null;
                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    var configured = TryResolveChild(root, levelName);
                    return configured is not null && HasLevelDat(configured) ? configured : null;
                }
            }

            var defaultWorld = Path.Combine(root, "world");
            if (HasLevelDat(defaultWorld)) return Path.GetFullPath(defaultWorld);

            // 客户端 saves 目录只有一个世界时仍可作为配置源，多个世界则必须由 Discovery 分拆。
            var children = Directory.EnumerateDirectories(root).Where(HasLevelDat).Take(2).ToArray();
            return children.Length == 1 ? Path.GetFullPath(children[0]) : null;
        }
        catch { return null; }
    }

    private static bool HasLevelDat(string path) => File.Exists(Path.Combine(path, "level.dat"));

    private static bool TryReadLevelName(string path, out string? levelName)
    {
        levelName = null;
        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                var separator = line.IndexOf('=');
                if (separator < 0 || !string.Equals(line[..separator].Trim(), "level-name", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(separator + 1)..].Trim();
                levelName = value.Length == 0 ? null : value;
                return true;
            }
            return true;
        }
        catch { return false; }
    }

    private static string? TryResolveChild(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        try
        {
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relativePath)));
            var relative = Path.GetRelativePath(root, candidate);
            return Path.IsPathRooted(relative)
                   || relative == ".."
                   || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? null
                : candidate;
        }
        catch { return null; }
    }
}
