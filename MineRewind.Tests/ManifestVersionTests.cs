using System.Text.Json;

namespace MineRewind.Tests;

[TestClass]
public sealed class ManifestVersionTests
{
    [TestMethod]
    public void ManifestDeclaresV3ApiAndStaticKindPolicy()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.AreEqual(3, root.GetProperty("manifestVersion").GetInt32());
        Assert.AreEqual("com.folderrewind.minerewind", root.GetProperty("pluginId").GetString());
        Assert.AreEqual("1.9.1", root.GetProperty("version").GetString());
        Assert.AreEqual(3, root.GetProperty("pluginApi").GetProperty("major").GetInt32());
        Assert.AreEqual(1, root.GetProperty("pluginApi").GetProperty("minor").GetInt32());
        Assert.AreEqual("settings.schema.json", root.GetProperty("settingsSchema").GetString());
        var kind = root.GetProperty("configKinds")[0];
        Assert.AreEqual("com.folderrewind.minerewind", kind.GetProperty("ownerId").GetString());
        Assert.AreEqual("minecraft-saves", kind.GetProperty("kindId").GetString());
        Assert.IsTrue(kind.GetProperty("localizedDisplayName").TryGetProperty("zh-CN", out _));
        Assert.IsTrue(kind.GetProperty("localizedDescription").TryGetProperty("en-US", out _));
        Assert.AreEqual("minecraft", kind.GetProperty("icon").GetString());
        Assert.AreEqual("rawWithWarnings", kind.GetProperty("backupFallback").GetString());
        Assert.AreEqual("required", kind.GetProperty("restoreCoordination").GetString());
        using var settingsDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "settings.schema.json")));
        var setting = settingsDocument.RootElement.GetProperty("settings")[0];
        Assert.IsTrue(setting.GetProperty("localizedDisplayName").TryGetProperty("zh-CN", out _));
        Assert.IsTrue(setting.GetProperty("localizedDescription").TryGetProperty("en-US", out _));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "discovery", "configReconciliation", "filePolicy", "backupScope",
                "backupConsistency", "folderMetadata", "restoreCoordinator",
                "pluginCommand", "knotLinkIntegration", "providerStateMigration"
            },
            root.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "configQuery", "backupRequest", "restoreRequest", "historyQuery",
                "knotLink", "temporaryStorage", "logging"
            },
            root.GetProperty("requestedHostServices").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
    }
}
