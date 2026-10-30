using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task EntityOnlyDimensionIsDiscoverableWithoutInventingTerrain()
    {
        using var world = TemporaryWorld.Create();
        var entities = Path.Combine(world.WorldPath, "DIM-1", "entities");
        Directory.CreateDirectory(entities);
        File.WriteAllBytes(Path.Combine(entities, "r.-1.2.mca"), new byte[8192]);
        var scan = WorldScanner.Scan(world.WorldPath, world.WorldPath);
        var dimension = scan.Dimensions.Single(d => d.Id == "minecraft:the_nether");
        Assert.HasCount(0, dimension.Regions);
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        var description = await fixture.Plugin.DescribeAsync(new(config, folder, Guid.NewGuid()), fixture.Invocation);
        Assert.IsTrue(description.Layers.Any(l => l.Id == "minecraft:the_nether"));
    }
}
