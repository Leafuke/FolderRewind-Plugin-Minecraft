using Microsoft.Win32;
using System.Globalization;

namespace MineRewind.Discovery.Locations;

internal static class NeteaseLocations
{
    public static void DiscoverKnown(MinecraftDiscoveryContext context, Action<MinecraftLocation> add)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                context.Check();
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Netease\MCLauncher", writable: false);
                var download = key?.GetValue("DownloadPath") as string;
                var root = MinecraftDiscoveryContext.Normalize(download);
                if (root is not null)
                    add(new(Path.Combine(root, "Game", ".minecraft"), MinecraftLocationKind.JavaGameRoot,
                        MinecraftEdition.Java, Name("NetEase Java", "网易 Java"), "netease"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { context.Warn("minerewind.discovery_registry_unreadable", "netease", @"HKCU\Software\Netease\MCLauncher"); }
        }
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming))
            add(new(Path.Combine(roaming, "MinecraftPE_Netease", "minecraftWorlds"),
                MinecraftLocationKind.WorldsDirectory, MinecraftEdition.Bedrock, Name("NetEase Bedrock", "网易 Bedrock"), "netease"));
    }

    private static string Name(string english, string chinese)
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? chinese : english;
}
