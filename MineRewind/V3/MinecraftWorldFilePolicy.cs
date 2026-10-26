using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

/// <summary>Default backup exclusions. Merge eligibility is proved by the Host against domain dependencies.</summary>
internal static class MinecraftWorldFilePolicy
{
    internal static IReadOnlyList<PathFilterRule> BackupExclusions { get; } = Array.AsReadOnly<PathFilterRule>(
    [
        new("session.lock", PathFilterTarget.File), new("voxy", PathFilterTarget.Directory),
        new("DistantHorizons.sqlite", PathFilterTarget.File),
        new("DistantHorizons.sqlite-shm", PathFilterTarget.File), new("DistantHorizons.sqlite-wal", PathFilterTarget.File)
    ]);

    internal static bool Excludes(string relativePath, bool isDirectory)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Take(isDirectory ? parts.Length : parts.Length - 1).Any(part => part.Equals("voxy", StringComparison.OrdinalIgnoreCase))) return true;
        return !isDirectory && BackupExclusions.Any(rule => rule.Target == PathFilterTarget.File
            && rule.Pattern.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSemanticNonDependency(PathFilterRule rule)
    {
        var pattern = rule.Pattern.Replace('\\', '/');
        return rule.SemanticsVersion == 1 && rule.Target == PathFilterTarget.Any
            && (pattern.Equals("session.lock", StringComparison.OrdinalIgnoreCase) || pattern.Equals("voxy/**", StringComparison.OrdinalIgnoreCase))
            || rule.SemanticsVersion == 2 && rule.Target == PathFilterTarget.Directory && pattern.Equals("voxy", StringComparison.OrdinalIgnoreCase);
    }
}
