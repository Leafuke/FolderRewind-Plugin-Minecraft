using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class WorldScannerTests
{
    [TestMethod]
    [DataRow(16)]
    [DataRow(17)]
    public void CustomDimensionDepthLimitCountsOnlyDimensionPath(int depth)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MineRewind.Scanner", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var parts = Enumerable.Repeat("d", depth).ToArray();
            var dimension = Path.Combine(root, "dimensions", "fixture", Path.Combine(parts));
            foreach (var component in new[] { "region", "entities", "poi", "data" })
                Directory.CreateDirectory(Path.Combine(dimension, component));
            var region = Path.Combine(dimension, "region", "r.0.0.mca");
            File.WriteAllBytes(region, new byte[8192]);
            if (depth > 16)
            {
                Assert.ThrowsExactly<WorldScanLimitException>(() => WorldScanner.Scan(root, root));
                return;
            }
            var world = WorldScanner.Scan(root, root);
            Assert.IsFalse(world.IsIncomplete);
            var found = world.Dimensions.Single();
            Assert.AreEqual("fixture:" + string.Join('/', parts), found.Id);
            Assert.AreEqual(region, found.Regions.Single().Value);
        }
        finally { Directory.Delete(root, true); }
    }
}
