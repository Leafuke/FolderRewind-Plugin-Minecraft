using Microsoft.Win32;

namespace MineRewind.Discovery;

/// <summary>Read-only ambient inputs. Fixtures replace these without touching accounts, registry or installed launchers.</summary>
internal sealed record MinecraftDiscoveryEnvironment
{
    internal static MinecraftDiscoveryEnvironment System { get; } = new();
    internal Func<Environment.SpecialFolder, string> Folder { get; init; } = Environment.GetFolderPath;
    internal Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;
    internal Func<string, string, string?> RegistryValue { get; init; } = ReadRegistry;
    internal Func<string, IEnumerable<string>> Entries { get; init; } = path => Directory.EnumerateFileSystemEntries(path);
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    internal int MaximumEntries { get; init; } = 100_000;
    internal int MaximumPathCharacters { get; init; } = 4 * 1024 * 1024;
    private static string? ReadRegistry(string path, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
        return key?.GetValue(name) as string;
    }
}
