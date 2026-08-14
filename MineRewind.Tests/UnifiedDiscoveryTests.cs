using FolderRewind.Models;

namespace MineRewind.Tests;

[TestClass]
public sealed class UnifiedDiscoveryTests
{
    [TestMethod]
    public async Task ProducesOneBackupSetPerMinecraftInstanceAndKeepsLegacyAdapter()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dotMinecraft = Path.Combine(root, ".minecraft");
            var directWorld = CreateWorld(Path.Combine(dotMinecraft, "saves", "Direct World"));
            var isolatedWorld = CreateWorld(Path.Combine(dotMinecraft, "versions", "Pack", "saves", "Pack World"));
            Directory.CreateDirectory(Path.Combine(dotMinecraft, "versions", "Pack", "mods"));
            File.WriteAllText(Path.Combine(dotMinecraft, "versions", "Pack", "mods", "example.jar"), "mod");

            var plugin = new MinecraftSavesPlugin();
            var result = await plugin.DiscoverAsync(
                new DiscoveryRequest
                {
                    Mode = DiscoveryRequestMode.UserRoots,
                    UserRoots = new[] { root }
                },
                progress: null,
                CancellationToken.None);

            Assert.AreEqual("com.folderrewind.minerewind", plugin.Descriptor.Id);
            Assert.IsTrue(plugin.Descriptor.IsSpecialized);
            Assert.HasCount(1, result.Candidates);
            Assert.HasCount(2, result.Candidates[0].BackupSets);
            Assert.AreEqual(2, result.Candidates[0].BackupSets.Select(set => set.Identity.SetId).Distinct().Count());
            Assert.IsTrue(result.Candidates[0].BackupSets.All(set =>
                set.Identity.ProviderId == "com.folderrewind.minerewind"
                && set.Identity.DefinitionId == "minecraft-java"));
            Assert.IsTrue(result.Candidates[0].BackupSets.All(set => set.SuggestedConfigType == "Minecraft Saves"));
            Assert.IsTrue(result.Candidates[0].BackupSets.SelectMany(set => set.Resources).All(resource =>
                resource.IsSpecializedProvider
                && resource.FixedRootExists
                && resource.IsSelectedByDefault
                && resource.Evidence.Any(evidence => evidence.Confidence == DiscoveryConfidence.High)));

            var legacyFolders = plugin.TryDiscoverManagedFolders(dotMinecraft, new Dictionary<string, string>());
            CollectionAssert.AreEquivalent(
                new[] { directWorld, isolatedWorld },
                legacyFolders.Select(folder => folder.Path).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task IgnoresDirectoriesWithoutLevelDat()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var dotMinecraft = Path.Combine(root, ".minecraft");
            Directory.CreateDirectory(Path.Combine(dotMinecraft, "saves", "Incomplete"));
            var plugin = new MinecraftSavesPlugin();

            var result = await plugin.DiscoverAsync(
                new DiscoveryRequest { UserRoots = new[] { dotMinecraft } },
                null,
                CancellationToken.None);

            Assert.IsEmpty(result.Candidates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorld(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "level.dat"), "level");
        return MinecraftInstanceDiscoveryPlanner.NormalizePath(path);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MineRewindTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
