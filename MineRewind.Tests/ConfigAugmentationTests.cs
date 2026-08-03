using FolderRewind.Models;
using FolderRewind.Services.Plugins;

namespace MineRewind.Tests;

[TestClass]
public sealed class ConfigAugmentationTests
{
    private readonly List<string> _temporaryRoots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string root in _temporaryRoots.OrderByDescending(static path => path.Length))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void AutoCreateFindsDefaultAndVersionInstancesAndIncludesMods()
    {
        string dotMinecraft = CreateDotMinecraft();
        string anchorWorld = CreateWorld(dotMinecraft, "Anchor", "Known");
        CreateWorld(dotMinecraft, null, "DefaultWorld");
        CreateWorld(dotMinecraft, "Pack", "PackWorld");
        Directory.CreateDirectory(Path.Combine(dotMinecraft, "mods"));
        Directory.CreateDirectory(Path.Combine(dotMinecraft, "versions", "Pack", "mods"));

        var result = Augment(
            [Config(anchorWorld)],
            autoDiscoverSaves: false,
            autoCreateConfigs: true);

        Assert.HasCount(2, result.ConfigsToAdd);
        var defaultConfig = result.ConfigsToAdd.Single(config => config.Name == "Minecraft - Default");
        var packConfig = result.ConfigsToAdd.Single(config => config.Name == "Minecraft - Pack");
        Assert.HasCount(2, defaultConfig.SourceFolders);
        Assert.HasCount(2, packConfig.SourceFolders);
        Assert.AreEqual(
            MinecraftInstanceDiscoveryPlanner.NormalizePath(dotMinecraft),
            defaultConfig.ExtendedProperties["MinecraftInstancePath"]);
        Assert.AreEqual(
            MinecraftInstanceDiscoveryPlanner.NormalizePath(Path.Combine(dotMinecraft, "versions", "Pack")),
            packConfig.ExtendedProperties["MinecraftInstancePath"]);
    }

    [TestMethod]
    public void DefaultConfigDoesNotAnchorDiscovery()
    {
        string dotMinecraft = CreateDotMinecraft();
        string anchorWorld = CreateWorld(dotMinecraft, "Anchor", "Known");
        CreateWorld(dotMinecraft, "Pack", "PackWorld");

        var result = Augment(
            [Config(anchorWorld, configType: "Default")],
            autoDiscoverSaves: false,
            autoCreateConfigs: true);

        Assert.IsFalse(result.Handled);
        Assert.IsEmpty(result.ConfigsToAdd);
    }

    [TestMethod]
    public void MixedConfigCoversEveryReferencedInstance()
    {
        string dotMinecraft = CreateDotMinecraft();
        string worldA = CreateWorld(dotMinecraft, "A", "WorldA");
        string worldB = CreateWorld(dotMinecraft, "B", "WorldB");
        CreateWorld(dotMinecraft, "C", "WorldC");
        var mixed = Config(worldA, worldB);

        var result = Augment([mixed], autoDiscoverSaves: false, autoCreateConfigs: true);

        Assert.HasCount(1, result.ConfigsToAdd);
        Assert.AreEqual("Minecraft - C", result.ConfigsToAdd[0].Name);
    }

    [TestMethod]
    public void ExactInstanceMarkerWinsFolderOwnership()
    {
        string dotMinecraft = CreateDotMinecraft();
        string worldA = CreateWorld(dotMinecraft, "A", "WorldA");
        string worldB = CreateWorld(dotMinecraft, "A", "WorldB");
        string worldC = CreateWorld(dotMinecraft, "A", "WorldC");
        CreateWorld(dotMinecraft, "A", "NewWorld");
        string otherInstance = CreateWorld(dotMinecraft, "B", "Other");
        string instancePath = Path.Combine(dotMinecraft, "versions", "A");

        var mixed = Config(worldA, otherInstance);
        var single = Config(worldB);
        var marked = Config(worldC);
        marked.ExtendedProperties["MinecraftInstancePath"] = instancePath;

        var result = Augment(
            [mixed, single, marked],
            autoDiscoverSaves: true,
            autoCreateConfigs: false);

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(marked.Id, result.Items[0].ConfigId);
        Assert.AreEqual("NewWorld", result.Items[0].FoldersToAdd.Single().DisplayName);
    }

    [TestMethod]
    [DataRow(false, false, 0, 0)]
    [DataRow(true, false, 1, 0)]
    [DataRow(false, true, 0, 1)]
    [DataRow(true, true, 1, 1)]
    public void DiscoverySettingsAreIndependent(
        bool autoDiscoverSaves,
        bool autoCreateConfigs,
        int expectedItems,
        int expectedConfigs)
    {
        string dotMinecraft = CreateDotMinecraft();
        string known = CreateWorld(dotMinecraft, "Anchor", "Known");
        CreateWorld(dotMinecraft, "Anchor", "NewWorld");
        CreateWorld(dotMinecraft, "Pack", "PackWorld");

        var result = Augment([Config(known)], autoDiscoverSaves, autoCreateConfigs);

        Assert.HasCount(expectedItems, result.Items);
        Assert.HasCount(expectedConfigs, result.ConfigsToAdd);
    }

    [TestMethod]
    public void AddedConfigMakesNextRunIdempotentButDeletionAllowsRediscovery()
    {
        string dotMinecraft = CreateDotMinecraft();
        string known = CreateWorld(dotMinecraft, "Anchor", "Known");
        CreateWorld(dotMinecraft, "Pack", "PackWorld");
        var anchor = Config(known);

        var first = Augment([anchor], autoDiscoverSaves: false, autoCreateConfigs: true);
        Assert.HasCount(1, first.ConfigsToAdd);

        var second = Augment(
            [anchor, first.ConfigsToAdd[0]],
            autoDiscoverSaves: false,
            autoCreateConfigs: true);
        Assert.IsEmpty(second.ConfigsToAdd);

        var afterDeletion = Augment([anchor], autoDiscoverSaves: false, autoCreateConfigs: true);
        Assert.HasCount(1, afterDeletion.ConfigsToAdd);
    }

    [TestMethod]
    public void NestedSavesAndModsOnlyInstancesAreNotCreated()
    {
        string dotMinecraft = CreateDotMinecraft();
        string anchor = CreateWorld(dotMinecraft, "Anchor", "Known");
        string nestedWorld = Path.Combine(dotMinecraft, "versions", "Nested", "child", "saves", "World");
        Directory.CreateDirectory(nestedWorld);
        File.WriteAllText(Path.Combine(nestedWorld, "level.dat"), string.Empty);
        Directory.CreateDirectory(Path.Combine(dotMinecraft, "versions", "ModsOnly", "mods"));

        var result = Augment(
            [Config(anchor)],
            autoDiscoverSaves: false,
            autoCreateConfigs: true);

        Assert.IsEmpty(result.ConfigsToAdd);
    }

    [TestMethod]
    public void ExistingAutoDiscoveryStillSupportsStandaloneSavesDirectories()
    {
        string root = Path.Combine(Path.GetTempPath(), "MineRewindTests", Guid.NewGuid().ToString("N"));
        string saves = Path.Combine(root, "saves");
        string known = Path.Combine(saves, "Known");
        string discovered = Path.Combine(saves, "Discovered");
        Directory.CreateDirectory(known);
        Directory.CreateDirectory(discovered);
        File.WriteAllText(Path.Combine(known, "level.dat"), string.Empty);
        File.WriteAllText(Path.Combine(discovered, "level.dat"), string.Empty);
        _temporaryRoots.Add(root);

        var result = Augment(
            [Config(known)],
            autoDiscoverSaves: true,
            autoCreateConfigs: false);

        Assert.HasCount(1, result.Items);
        Assert.AreEqual(discovered, result.Items[0].FoldersToAdd.Single().Path);
    }

    [TestMethod]
    public void SettingsChangeTriggersWhenEitherFeatureBecomesEnabled()
    {
        var plugin = new MinecraftSavesPlugin();

        Assert.IsTrue(plugin.ShouldAugmentAfterSettingsChange(
            Settings(("AutoDiscoverSaves", "false"), ("AutoCreateConfigs", "false")),
            Settings(("AutoDiscoverSaves", "true"), ("AutoCreateConfigs", "false"))));
        Assert.IsTrue(plugin.ShouldAugmentAfterSettingsChange(
            Settings(("AutoDiscoverSaves", "true"), ("AutoCreateConfigs", "false")),
            Settings(("AutoDiscoverSaves", "true"), ("AutoCreateConfigs", "true"))));
        Assert.IsFalse(plugin.ShouldAugmentAfterSettingsChange(
            Settings(("AutoDiscoverSaves", "true"), ("AutoCreateConfigs", "true")),
            Settings(("AutoDiscoverSaves", "true"), ("AutoCreateConfigs", "true"))));
    }

    private PluginConfigAugmentationResult Augment(
        IReadOnlyList<BackupConfig> configs,
        bool autoDiscoverSaves,
        bool autoCreateConfigs)
    {
        var plugin = new MinecraftSavesPlugin();
        return plugin.AugmentConfigs(
            new PluginConfigAugmentationRequest
            {
                Reason = PluginConfigAugmentationReason.Startup,
                Configs = configs
            },
            Settings(
                ("AutoDiscoverSaves", autoDiscoverSaves.ToString()),
                ("AutoCreateConfigs", autoCreateConfigs.ToString())));
    }

    private string CreateDotMinecraft()
    {
        string root = Path.Combine(Path.GetTempPath(), "MineRewindTests", Guid.NewGuid().ToString("N"));
        string dotMinecraft = Path.Combine(root, ".minecraft");
        Directory.CreateDirectory(dotMinecraft);
        _temporaryRoots.Add(root);
        return dotMinecraft;
    }

    private static string CreateWorld(string dotMinecraft, string? version, string worldName)
    {
        string instance = string.IsNullOrWhiteSpace(version)
            ? dotMinecraft
            : Path.Combine(dotMinecraft, "versions", version);
        string world = Path.Combine(instance, "saves", worldName);
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "level.dat"), string.Empty);
        return world;
    }

    private static BackupConfig Config(string path, string? secondPath = null, string configType = "Minecraft Saves")
    {
        var config = new BackupConfig { ConfigType = configType };
        config.SourceFolders.Add(new ManagedFolder { Path = path, DisplayName = Path.GetFileName(path) });
        if (!string.IsNullOrWhiteSpace(secondPath))
        {
            config.SourceFolders.Add(new ManagedFolder { Path = secondPath, DisplayName = Path.GetFileName(secondPath) });
        }

        return config;
    }

    private static IReadOnlyDictionary<string, string> Settings(params (string Key, string Value)[] entries)
        => entries.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
}
