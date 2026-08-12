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
        Assert.AreEqual("1.9.0", root.GetProperty("version").GetString());
        Assert.AreEqual(3, root.GetProperty("pluginApi").GetProperty("major").GetInt32());
        Assert.AreEqual(0, root.GetProperty("pluginApi").GetProperty("minor").GetInt32());
        Assert.AreEqual("settings.schema.json", root.GetProperty("settingsSchema").GetString());
        var kind = root.GetProperty("configKinds")[0];
        Assert.AreEqual("com.folderrewind.minerewind", kind.GetProperty("ownerId").GetString());
        Assert.AreEqual("minecraft-saves", kind.GetProperty("kindId").GetString());
        Assert.AreEqual("rawWithWarnings", kind.GetProperty("backupFallback").GetString());
        Assert.AreEqual("required", kind.GetProperty("restoreCoordination").GetString());
    }
}
