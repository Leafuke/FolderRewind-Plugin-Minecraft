namespace FolderRewind.Models
{
    public sealed class BackupConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string ConfigType { get; set; } = "Minecraft Saves";
        public string IconGlyph { get; set; } = string.Empty;
        public List<ManagedFolder> SourceFolders { get; set; } = [];
        public Dictionary<string, string> ExtendedProperties { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class ManagedFolder
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string CoverImagePath { get; set; } = string.Empty;
    }
}

namespace FolderRewind.Services
{
    public static class FolderNameConflictService
    {
        public static string ResolveDisplayName(FolderRewind.Models.ManagedFolder? folder)
            => folder == null
                ? string.Empty
                : ResolveDisplayName(folder.DisplayName, folder.Path);

        public static string ResolveDisplayName(string? displayName, string? path)
            => !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : Path.GetFileName(path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty;
    }

    public static class LogService
    {
        public static void LogWarning(string message, string? source = null)
        {
        }
    }

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
    public enum PluginConfigAugmentationReason
    {
        Startup,
        SettingsEnabled
    }

    public sealed class PluginConfigAugmentationRequest
    {
        public PluginConfigAugmentationReason Reason { get; init; }
        public IReadOnlyList<FolderRewind.Models.BackupConfig> Configs { get; init; }
            = Array.Empty<FolderRewind.Models.BackupConfig>();
    }

    public sealed class PluginConfigAugmentationItem
    {
        public string ConfigId { get; init; } = string.Empty;
        public IReadOnlyList<FolderRewind.Models.ManagedFolder> FoldersToAdd { get; init; }
            = Array.Empty<FolderRewind.Models.ManagedFolder>();
    }

    public sealed class PluginConfigAugmentationResult
    {
        public bool Handled { get; init; }
        public IReadOnlyList<PluginConfigAugmentationItem> Items { get; init; }
            = Array.Empty<PluginConfigAugmentationItem>();
        public IReadOnlyList<FolderRewind.Models.BackupConfig> ConfigsToAdd { get; init; }
            = Array.Empty<FolderRewind.Models.BackupConfig>();
    }

    public sealed class PluginCreateConfigResult
    {
        public bool Handled { get; set; }
        public IReadOnlyList<FolderRewind.Models.BackupConfig>? CreatedConfigs { get; set; }
        public string? Message { get; set; }
    }

    public sealed class PluginInstallManifest
    {
        public string Id { get; init; } = string.Empty;
    }

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
        private const string ConfigTypeName = "Minecraft Saves";
        private const string AutoDiscoverSavesSettingKey = "AutoDiscoverSaves";
        private const string AutoCreateConfigsSettingKey = "AutoCreateConfigs";

        public FolderRewind.Services.Plugins.PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.folderrewind.minerewind"
        };

        private static bool EnsureRequiredFilters(FolderRewind.Models.BackupConfig config) => false;

        private static bool GetBoolSetting(
            IReadOnlyDictionary<string, string> settings,
            string key,
            bool defaultValue)
            => settings.TryGetValue(key, out string? value)
                ? bool.TryParse(value, out bool parsed) && parsed
                : defaultValue;

        private static string Localize(string key) => key;

        private static string LocalizeFormat(string key, params object[] args)
            => $"{key}: {string.Join(", ", args)}";
    }
}
