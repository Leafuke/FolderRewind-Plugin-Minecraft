using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MineRewind;

public partial class MinecraftSavesPlugin
{
    public PluginConfigAugmentationResult AugmentConfigs(
        PluginConfigAugmentationRequest request,
        IReadOnlyDictionary<string, string> settingsValues)
    {
        if (!GetBoolSetting(settingsValues, AutoDiscoverSavesSettingKey, true))
        {
            return new PluginConfigAugmentationResult { Handled = false };
        }

        var items = new List<PluginConfigAugmentationItem>();

        foreach (var config in request.Configs.Where(IsEligibleMinecraftConfig))
        {
            var foldersToAdd = DiscoverAugmentedFolders(config);
            if (foldersToAdd.Count == 0)
            {
                continue;
            }

            items.Add(new PluginConfigAugmentationItem
            {
                ConfigId = config.Id,
                FoldersToAdd = foldersToAdd
            });
        }

        return new PluginConfigAugmentationResult
        {
            Handled = items.Count > 0,
            Items = items
        };
    }

    public bool ShouldAugmentAfterSettingsChange(
        IReadOnlyDictionary<string, string> previousSettings,
        IReadOnlyDictionary<string, string> currentSettings)
    {
        bool wasEnabled = GetBoolSetting(previousSettings, AutoDiscoverSavesSettingKey, true);
        bool isEnabled = GetBoolSetting(currentSettings, AutoDiscoverSavesSettingKey, true);
        return !wasEnabled && isEnabled;
    }

    private bool IsEligibleMinecraftConfig(BackupConfig config)
    {
        if (config == null || !CanHandleConfigType(config.ConfigType))
        {
            return false;
        }

        if (config.ExtendedProperties.TryGetValue("Plugin", out var pluginId)
            && string.Equals(pluginId, Manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return config.SourceFolders.Any(folder => IsMinecraftWorldFolder(folder?.Path));
    }

    private IReadOnlyList<ManagedFolder> DiscoverAugmentedFolders(BackupConfig config)
    {
        var results = new List<ManagedFolder>();
        var knownPaths = new HashSet<string>(
            config.SourceFolders.Select(folder => folder.Path ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        var knownDisplayNames = new HashSet<string>(
            config.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
            StringComparer.OrdinalIgnoreCase);
        var scannedSavesDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in config.SourceFolders.Where(folder => folder != null && IsMinecraftWorldFolder(folder.Path)))
        {
            string? savesDir = TryGetSiblingSavesDirectory(folder.Path);
            if (string.IsNullOrWhiteSpace(savesDir) || !scannedSavesDirs.Add(savesDir))
            {
                continue;
            }

            foreach (var candidate in DiscoverFromSavesDirectory(savesDir))
            {
                string candidatePath = candidate.Path?.Trim() ?? string.Empty;
                string candidateName = FolderNameConflictService.ResolveDisplayName(candidate);

                if (string.IsNullOrWhiteSpace(candidatePath) || knownPaths.Contains(candidatePath) || knownDisplayNames.Contains(candidateName))
                {
                    continue;
                }

                knownPaths.Add(candidatePath);
                knownDisplayNames.Add(candidateName);
                results.Add(candidate);
            }
        }

        return results;
    }

    private static bool IsMinecraftWorldFolder(string? worldPath)
    {
        return !string.IsNullOrWhiteSpace(worldPath)
            && File.Exists(Path.Combine(worldPath, "level.dat"));
    }

    private static string? TryGetSiblingSavesDirectory(string? worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath))
        {
            return null;
        }

        string? parent = Directory.GetParent(worldPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        return string.Equals(Path.GetFileName(parent), "saves", StringComparison.OrdinalIgnoreCase)
            ? parent
            : null;
    }
}
