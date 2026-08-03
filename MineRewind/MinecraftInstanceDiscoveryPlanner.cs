using FolderRewind.Models;
using System.IO;

namespace MineRewind;

internal sealed class MinecraftInstanceDescriptor
{
    public string DotMinecraftPath { get; init; } = string.Empty;
    public string InstancePath { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string SavesPath { get; init; } = string.Empty;
    public string ModsPath { get; init; } = string.Empty;
    public IReadOnlyList<string> WorldPaths { get; init; } = Array.Empty<string>();
}

internal static class MinecraftInstanceDiscoveryPlanner
{
    private const string MinecraftConfigType = "Minecraft Saves";

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static bool IsMinecraftConfig(BackupConfig? config)
        => config != null
            && string.Equals(config.ConfigType, MinecraftConfigType, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FindDotMinecraftRoots(IEnumerable<BackupConfig> configs)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configs.Where(IsMinecraftConfig))
        {
            foreach (var folder in config.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
            {
                string root = FindDotMinecraftRoot(folder?.Path);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    roots.Add(root);
                }
            }
        }

        return roots.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string FindDotMinecraftRoot(string? sourcePath)
    {
        string normalized = NormalizePath(sourcePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        try
        {
            DirectoryInfo? current = new(normalized);
            while (current != null)
            {
                if (string.Equals(current.Name, ".minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizePath(current.FullName);
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    public static string InferInstancePath(string? sourcePath)
    {
        string root = FindDotMinecraftRoot(sourcePath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        string normalizedSource = NormalizePath(sourcePath);
        if (string.Equals(normalizedSource, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, normalizedSource);
        }
        catch
        {
            return string.Empty;
        }

        string[] segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0
            && string.Equals(segments[0], "versions", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Length >= 2
                ? NormalizePath(Path.Combine(root, "versions", segments[1]))
                : string.Empty;
        }

        return root;
    }

    public static IReadOnlySet<string> GetReferencedInstances(BackupConfig config)
    {
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsMinecraftConfig(config))
        {
            return instances;
        }

        if (config.ExtendedProperties != null
            && config.ExtendedProperties.TryGetValue("MinecraftInstancePath", out string? markedPath))
        {
            string normalizedMarker = NormalizePath(markedPath);
            if (!string.IsNullOrWhiteSpace(normalizedMarker))
            {
                instances.Add(normalizedMarker);
            }
        }

        foreach (var folder in config.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
        {
            string instance = InferInstancePath(folder?.Path);
            if (!string.IsNullOrWhiteSpace(instance))
            {
                instances.Add(instance);
            }
        }

        return instances;
    }

    public static IReadOnlyList<MinecraftInstanceDescriptor> DiscoverInstances(
        string dotMinecraftPath,
        Action<string>? logWarning = null)
    {
        string root = NormalizePath(dotMinecraftPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<MinecraftInstanceDescriptor>();
        }

        var results = new List<MinecraftInstanceDescriptor>();
        var direct = TryDiscoverInstance(root, root, "Default", logWarning);
        if (direct != null)
        {
            results.Add(direct);
        }

        string versionsPath = Path.Combine(root, "versions");
        if (!Directory.Exists(versionsPath))
        {
            return results;
        }

        try
        {
            foreach (string versionPath in Directory.EnumerateDirectories(versionsPath)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                string versionName = Path.GetFileName(versionPath);
                var instance = TryDiscoverInstance(root, versionPath, versionName, logWarning);
                if (instance != null)
                {
                    results.Add(instance);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logWarning?.Invoke($"Failed to enumerate Minecraft versions under '{versionsPath}': {ex.Message}");
        }

        return results;
    }

    public static MinecraftInstanceDescriptor? TryDiscoverInstance(
        string dotMinecraftPath,
        string instancePath,
        string versionName,
        Action<string>? logWarning = null)
    {
        string normalizedInstance = NormalizePath(instancePath);
        string savesPath = Path.Combine(normalizedInstance, "saves");
        if (!Directory.Exists(savesPath))
        {
            return null;
        }

        var worlds = new List<string>();
        try
        {
            foreach (string worldPath in Directory.EnumerateDirectories(savesPath)
                         .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(worldPath, "level.dat")))
                {
                    worlds.Add(NormalizePath(worldPath));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logWarning?.Invoke($"Failed to enumerate Minecraft saves under '{savesPath}': {ex.Message}");
            return null;
        }

        if (worlds.Count == 0)
        {
            return null;
        }

        string modsPath = Path.Combine(normalizedInstance, "mods");
        return new MinecraftInstanceDescriptor
        {
            DotMinecraftPath = NormalizePath(dotMinecraftPath),
            InstancePath = normalizedInstance,
            VersionName = versionName,
            SavesPath = NormalizePath(savesPath),
            ModsPath = Directory.Exists(modsPath) ? NormalizePath(modsPath) : string.Empty,
            WorldPaths = worlds
        };
    }
}
