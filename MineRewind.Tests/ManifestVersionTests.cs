using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind;

namespace MineRewind.Tests;

[TestClass]
public sealed class ManifestVersionTests
{
    [TestMethod]
    public void ManifestMatchesBuiltPluginAndDeclaresUniqueCapabilities()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var declaredVersion = Version.Parse(root.GetProperty("version").GetString()!);
        var assemblyVersion = typeof(MinecraftSavesPlugin).Assembly.GetName().Version!;
        Assert.AreEqual(assemblyVersion.Major, declaredVersion.Major);
        Assert.AreEqual(assemblyVersion.Minor, declaredVersion.Minor);
        Assert.AreEqual(assemblyVersion.Build, declaredVersion.Build);
        var api = root.GetProperty("pluginApi");
        var requirement = new PluginApiVersion(api.GetProperty("major").GetInt32(), api.GetProperty("minor").GetInt32());
        Assert.IsTrue(requirement.IsSatisfiedBy(PluginApiVersion.HostVersion));
        var kind = root.GetProperty("configKinds")[0];
        Assert.AreEqual(root.GetProperty("pluginId").GetString(), kind.GetProperty("ownerId").GetString());
        using var settingsDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, root.GetProperty("settingsSchema").GetString()!)));
        var setting = settingsDocument.RootElement.GetProperty("settings")[0];
        foreach (var localized in new[] { kind.GetProperty("localizedDisplayName"), kind.GetProperty("localizedDescription"), setting.GetProperty("localizedDisplayName"), setting.GetProperty("localizedDescription") })
            Assert.IsTrue(localized.EnumerateObject().All(value => !string.IsNullOrWhiteSpace(value.Value.GetString())));
        foreach (var declarations in new[] { root.GetProperty("capabilities"), root.GetProperty("requestedHostServices") })
        {
            var values = declarations.EnumerateArray().Select(value => value.GetString()).ToArray();
            Assert.IsTrue(values.All(value => !string.IsNullOrWhiteSpace(value)));
            Assert.AreEqual(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
