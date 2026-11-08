namespace MineRewind.World;

/// <summary>
/// 将受管源目录解析为真实世界目录；同时覆盖客户端世界与 dedicated server 的 level-name 语义。
/// </summary>
public static class WorldLocator
{
    public static string? TryResolveWorldPath(string? configuredRootPath)
        => Resolve(configuredRootPath, default, null);
    internal static string? ResolveForDisplay(string? configuredRootPath, CancellationToken token, WorldScanBudget budget)
        => Resolve(configuredRootPath, token, budget);
    private static string? Resolve(string? configuredRootPath, CancellationToken token, WorldScanBudget? budget)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(configuredRootPath) || !Directory.Exists(configuredRootPath)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRootPath));
            if (HasLevelDat(root)) return root;

            var properties = Path.Combine(root, "server.properties");
            if (File.Exists(properties))
            {
                if (budget is not null && new FileInfo(properties).Length > 65536) { budget.MarkIncomplete(); return null; }
                if (!TryReadLevelName(properties, out var levelName, token, budget)) return null;
                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    var configured = TryResolveChild(root, levelName);
                    return configured is not null && HasLevelDat(configured) ? configured : null;
                }
            }

            var defaultWorld = Path.Combine(root, "world");
            if (HasLevelDat(defaultWorld)) return Path.GetFullPath(defaultWorld);

            // 客户端 saves 目录只有一个世界时仍可作为配置源，多个世界则必须由 Discovery 分拆。
            var entries = budget is null ? Directory.EnumerateDirectories(root)
                : budget.Entries(root, root, token, rejectLinks: false).Where(Directory.Exists);
            var children = entries.Where(HasLevelDat).Take(2).ToArray();
            return children.Length == 1 ? Path.GetFullPath(children[0]) : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { budget?.MarkIncomplete(); return null; }
    }

    private static bool HasLevelDat(string path) => File.Exists(Path.Combine(path, "level.dat"));

    private static bool TryReadLevelName(string path, out string? levelName, CancellationToken token, WorldScanBudget? budget)
    {
        levelName = null;
        try
        {
            // A bounded byte snapshot prevents a growing file or a single huge line bypassing the quota.
            var lines = budget is not null ? ReadProperties(path, token) : File.ReadLines(path);
            foreach (var rawLine in lines)
            {
                token.ThrowIfCancellationRequested();
                if (!MinecraftPathGrammar.TryLevelName(rawLine, out levelName)) continue;
                return true;
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { budget?.MarkIncomplete(); return false; }
    }
    internal static IEnumerable<string> ReadProperties(string path, CancellationToken token)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[65537]; var used = 0;
        while (used < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(bytes, used, bytes.Length - used); if (read == 0) break; used += read;
        }
        if (used > 65536) throw new WorldScanLimitException("Server properties exceeds the display read budget.");
        using var text = new StreamReader(new MemoryStream(bytes, 0, used), detectEncodingFromByteOrderMarks: true);
        while (text.ReadLine() is { } line) { token.ThrowIfCancellationRequested(); yield return line; }
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
