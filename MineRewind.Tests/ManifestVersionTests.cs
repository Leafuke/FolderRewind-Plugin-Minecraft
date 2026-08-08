using System.Text.Json;

namespace MineRewind.Tests;

[TestClass]
public sealed class ManifestVersionTests
{
    [TestMethod]
    public void UnifiedDiscoveryReleaseRequiresFeatureHostVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.AreEqual("1.9.0", root.GetProperty("Version").GetString());
        Assert.AreEqual("1.9.0", root.GetProperty("MinHostVersion").GetString());
    }
}
