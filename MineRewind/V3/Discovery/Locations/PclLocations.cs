using Microsoft.Win32;

namespace MineRewind.Discovery.Locations;

internal static class PclLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var branch in new[] { "PCL", "PCLDebug" })
        {
            context.Check();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\" + branch, writable: false);
                // Missing LaunchFolders is normal when PCL has only default directories.
                PclWorkspaceLocations.ReadFolders(key?.GetValue("LaunchFolders") as string, "pcl2", context, add);
                PclWorkspaceLocations.FromDownloadCache(key?.GetValue("CacheDownloadFolder") as string, "pcl2", context, add);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { context.Warn("minerewind.discovery_registry_unreadable", "pcl2", @"HKCU\Software\" + branch); }
        }
    }
}
