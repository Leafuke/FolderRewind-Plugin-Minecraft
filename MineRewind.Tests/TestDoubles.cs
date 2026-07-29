namespace FolderRewind.Models
{
    public sealed class BackupConfig
    {
        public string ConfigType { get; set; } = "Minecraft Saves";
    }

    public sealed class ManagedFolder
    {
        public string Path { get; set; } = string.Empty;
    }
}

namespace FolderRewind.Services
{
    public static class BackupStoragePathService
    {
        public static bool IsPathInsideRoot(string candidatePath, string rootPath)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedCandidate = Path.GetFullPath(candidatePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || normalizedCandidate.StartsWith(
                        normalizedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}

namespace FolderRewind.Services.Plugins
{
    public enum PluginSettingType
    {
        Boolean,
        MultilineString
    }

    public enum PluginBackupRuleMergeMode
    {
        Append,
        Replace
    }

    public sealed class PluginSettingDefinition
    {
        public string Key { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public PluginSettingType Type { get; init; }
        public string DefaultValue { get; init; } = string.Empty;
        public bool IsRequired { get; init; }
    }

    public sealed class PluginBackupScopeDefinition
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public IReadOnlyList<PluginSettingDefinition> Parameters { get; init; } = [];
    }

    public sealed class PluginBackupScopeContext
    {
        public string ScopeId { get; init; } = string.Empty;
        public IReadOnlyDictionary<string, string> Parameters { get; init; }
            = new Dictionary<string, string>();
    }

    public sealed class PluginBackupFilterContribution
    {
        public bool UseWhitelistMode { get; init; }
        public IReadOnlyList<string> BackupWhitelist { get; init; } = [];
    }

    public sealed class PluginBackupScopeResolution
    {
        private PluginBackupScopeResolution()
        {
        }

        public static PluginBackupScopeResolution Invalid(string errorCode, string errorMessage) => new();

        public static PluginBackupScopeResolution NotApplicable() => new();

        public static PluginBackupScopeResolution Applied(
            PluginBackupFilterContribution contribution,
            PluginBackupRuleMergeMode mergeMode) => new();
    }
}

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        public bool CanHandleConfigType(string configType)
            => string.Equals(configType, "Minecraft Saves", StringComparison.OrdinalIgnoreCase);

        private static string Localize(string key) => key;

        private static string LocalizeFormat(string key, params object[] args)
            => $"{key}: {string.Join(", ", args)}";
    }
}
