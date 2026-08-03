using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Collections.Generic;
using System.Linq;

namespace MineRewind;

public partial class MinecraftSavesPlugin
{
    public PluginConfigAugmentationResult AugmentConfigs(
        PluginConfigAugmentationRequest request,
        IReadOnlyDictionary<string, string> settingsValues)
    {
        bool autoDiscoverSaves = GetBoolSetting(settingsValues, AutoDiscoverSavesSettingKey, true);
        bool autoCreateConfigs = GetBoolSetting(settingsValues, AutoCreateConfigsSettingKey, false);
        if (!autoDiscoverSaves && !autoCreateConfigs)
        {
            return new PluginConfigAugmentationResult { Handled = false };
        }

        var configs = (request.Configs ?? Array.Empty<BackupConfig>())
            .Where(static config => config != null)
            .ToList();
        var minecraftConfigs = configs
            .Where(MinecraftInstanceDiscoveryPlanner.IsMinecraftConfig)
            .ToList();
        if (minecraftConfigs.Count == 0)
        {
            return new PluginConfigAugmentationResult { Handled = false };
        }

        var instancesByConfig = minecraftConfigs.ToDictionary(
            static config => config,
            MinecraftInstanceDiscoveryPlanner.GetReferencedInstances);
        var representedInstances = new HashSet<string>(
            instancesByConfig.Values.SelectMany(static instances => instances),
            StringComparer.OrdinalIgnoreCase);
        var knownPaths = new HashSet<string>(
            configs.SelectMany(config => config.SourceFolders ?? Enumerable.Empty<ManagedFolder>())
                .Select(folder => MinecraftInstanceDiscoveryPlanner.NormalizePath(folder?.Path))
                .Where(static path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);

        var discoveredInstances = MinecraftInstanceDiscoveryPlanner
            .FindDotMinecraftRoots(minecraftConfigs)
            .SelectMany(root => MinecraftInstanceDiscoveryPlanner.DiscoverInstances(root, LogDiscoveryWarning))
            .GroupBy(static instance => instance.InstancePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static instance => instance.InstancePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var foldersByConfig = new Dictionary<BackupConfig, List<ManagedFolder>>();
        if (autoDiscoverSaves)
        {
            foreach (var instance in discoveredInstances.Where(instance => representedInstances.Contains(instance.InstancePath)))
            {
                var owner = SelectInstanceOwner(instance.InstancePath, minecraftConfigs, instancesByConfig);
                if (owner == null)
                {
                    continue;
                }

                AddNewWorlds(
                    owner,
                    instance.WorldPaths.Select(CreateManagedFolder),
                    knownPaths,
                    foldersByConfig);
            }

            foreach (var group in FindStandaloneSavesOwners(minecraftConfigs))
            {
                try
                {
                    AddNewWorlds(
                        group.Owner,
                        DiscoverFromSavesDirectory(group.SavesPath),
                        knownPaths,
                        foldersByConfig);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogDiscoveryWarning($"Failed to enumerate Minecraft saves under '{group.SavesPath}': {ex.Message}");
                }
            }
        }

        var configsToAdd = new List<BackupConfig>();
        if (autoCreateConfigs)
        {
            foreach (var instance in discoveredInstances.Where(instance => !representedInstances.Contains(instance.InstancePath)))
            {
                var candidate = CreateConfigForInstance(instance);
                var candidatePaths = candidate.SourceFolders
                    .Select(folder => MinecraftInstanceDiscoveryPlanner.NormalizePath(folder.Path))
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .ToArray();
                if (candidatePaths.Length == 0 || candidatePaths.Any(knownPaths.Contains))
                {
                    continue;
                }

                configsToAdd.Add(candidate);
                representedInstances.Add(instance.InstancePath);
                foreach (string path in candidatePaths)
                {
                    knownPaths.Add(path);
                }
            }
        }

        var items = foldersByConfig
            .Where(static pair => pair.Value.Count > 0)
            .Select(static pair => new PluginConfigAugmentationItem
            {
                ConfigId = pair.Key.Id,
                FoldersToAdd = pair.Value
            })
            .ToArray();

        return new PluginConfigAugmentationResult
        {
            Handled = items.Length > 0 || configsToAdd.Count > 0,
            Items = items,
            ConfigsToAdd = configsToAdd
        };
    }

    public bool ShouldAugmentAfterSettingsChange(
        IReadOnlyDictionary<string, string> previousSettings,
        IReadOnlyDictionary<string, string> currentSettings)
    {
        bool savesWereEnabled = GetBoolSetting(previousSettings, AutoDiscoverSavesSettingKey, true);
        bool savesAreEnabled = GetBoolSetting(currentSettings, AutoDiscoverSavesSettingKey, true);
        bool configsWereEnabled = GetBoolSetting(previousSettings, AutoCreateConfigsSettingKey, false);
        bool configsAreEnabled = GetBoolSetting(currentSettings, AutoCreateConfigsSettingKey, false);
        return (!savesWereEnabled && savesAreEnabled)
            || (!configsWereEnabled && configsAreEnabled);
    }

    private static BackupConfig? SelectInstanceOwner(
        string instancePath,
        IReadOnlyList<BackupConfig> configs,
        IReadOnlyDictionary<BackupConfig, IReadOnlySet<string>> instancesByConfig)
    {
        return configs
            .Select((config, index) => new
            {
                Config = config,
                Index = index,
                Instances = instancesByConfig[config]
            })
            .Where(entry => entry.Instances.Contains(instancePath))
            .OrderBy(entry => GetInstanceOwnerRank(entry.Config, entry.Instances, instancePath))
            .ThenBy(static entry => entry.Index)
            .Select(static entry => entry.Config)
            .FirstOrDefault();
    }

    private static int GetInstanceOwnerRank(
        BackupConfig config,
        IReadOnlySet<string> referencedInstances,
        string instancePath)
    {
        if (config.ExtendedProperties != null
            && config.ExtendedProperties.TryGetValue("MinecraftInstancePath", out string? markedPath)
            && string.Equals(
                MinecraftInstanceDiscoveryPlanner.NormalizePath(markedPath),
                instancePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return referencedInstances.Count == 1 ? 1 : 2;
    }

    private static IReadOnlyList<(string SavesPath, BackupConfig Owner)> FindStandaloneSavesOwners(
        IReadOnlyList<BackupConfig> configs)
    {
        return configs
            .SelectMany((config, index) => config.SourceFolders.Select(folder => new
            {
                Config = config,
                Index = index,
                SavesPath = TryGetStandaloneSavesDirectory(folder?.Path)
            }))
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.SavesPath))
            .GroupBy(static entry => entry.SavesPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var owner = group.OrderBy(static entry => entry.Index).First();
                return (owner.SavesPath, owner.Config);
            })
            .ToArray();
    }

    private static string TryGetStandaloneSavesDirectory(string? worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath)
            || !File.Exists(Path.Combine(worldPath, "level.dat"))
            || !string.IsNullOrWhiteSpace(MinecraftInstanceDiscoveryPlanner.FindDotMinecraftRoot(worldPath)))
        {
            return string.Empty;
        }

        string? parent = Directory.GetParent(worldPath)?.FullName;
        return !string.IsNullOrWhiteSpace(parent)
            && string.Equals(Path.GetFileName(parent), "saves", StringComparison.OrdinalIgnoreCase)
                ? MinecraftInstanceDiscoveryPlanner.NormalizePath(parent)
                : string.Empty;
    }

    private static void AddNewWorlds(
        BackupConfig owner,
        IEnumerable<ManagedFolder> candidates,
        ISet<string> knownPaths,
        IDictionary<BackupConfig, List<ManagedFolder>> foldersByConfig)
    {
        var knownDisplayNames = new HashSet<string>(
            owner.SourceFolders.Select(FolderNameConflictService.ResolveDisplayName),
            StringComparer.OrdinalIgnoreCase);
        if (!foldersByConfig.TryGetValue(owner, out var folders))
        {
            folders = new List<ManagedFolder>();
            foldersByConfig[owner] = folders;
        }
        else
        {
            knownDisplayNames.UnionWith(folders.Select(FolderNameConflictService.ResolveDisplayName));
        }

        foreach (var candidate in candidates)
        {
            string normalizedPath = MinecraftInstanceDiscoveryPlanner.NormalizePath(candidate.Path);
            if (knownPaths.Contains(normalizedPath))
            {
                continue;
            }

            string displayName = FolderNameConflictService.ResolveDisplayName(candidate);
            if (knownDisplayNames.Contains(displayName))
            {
                LogDiscoveryWarning(
                    $"Skipped world '{candidate.Path}' because config '{owner.Name}' already contains display name '{displayName}'.");
                continue;
            }

            knownPaths.Add(normalizedPath);
            knownDisplayNames.Add(displayName);
            folders.Add(candidate);
        }
    }

    private static void LogDiscoveryWarning(string message)
        => LogService.LogWarning($"[MineRewind] {message}", "MineRewind");
}
