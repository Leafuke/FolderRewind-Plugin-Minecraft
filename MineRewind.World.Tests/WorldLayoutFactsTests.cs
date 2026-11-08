using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class WorldLayoutFactsTests
{
    [TestMethod]
    [DataRow("region/r.-1.2.mca", ".", "minecraft:overworld", -1, 2, ChunkComponentKind.Terrain)]
    [DataRow("world/DIM-1/entities/r.0.-1.mca", "world", "minecraft:the_nether", 0, -1, ChunkComponentKind.Entities)]
    [DataRow("world/dimensions/mod/deep/moon/poi/r.2.3.mca", "world", "mod:deep/moon", 2, 3, ChunkComponentKind.Poi)]
    [DataRow("world/nested/region/r.0.0.mca", "world/nested", "minecraft:overworld", 0, 0, ChunkComponentKind.Terrain)]
    public void FixedInventoryKeepsPersistedWorldAndRegionIdentities(string path, string world, string dimension, int x, int z, ChunkComponentKind component)
    {
        var input = new[] { "level.dat", "world/level.dat", "world/nested/level.dat", path };
        var facts = WorldLayoutReader.FromInventory(input);
        var actual = facts.Single().Dimensions.Single();
        Assert.AreEqual(world, facts.Single().WorldId); Assert.AreEqual(dimension, actual.Id);
        Assert.AreEqual(new WorldRegionFile(component, new(x, z), path), actual.RegionFiles.Single());
        Assert.IsTrue(MergeRegion.TryAddress(path, MinecraftPathGrammar.WorldRoots(input), out var address));
        Assert.AreEqual(new RegionAddress(world, dimension, x, z, component), address);
    }

    [TestMethod]
    public void DirectoryFactsCountComponentsAndPaperSiblingsWithoutUnrelatedFiles()
    {
        using var source = new Tree();
        source.File("world/level.dat");
        source.File("world/region/r.0.0.mca"); source.File("world/entities/r.0.0.mca"); source.File("world/poi/r.-1.0.mca");
        source.File("world/unrelated/r.0.0.mca"); source.File("world/region/nested/r.0.0.mca");
        source.File("world_nether/DIM-1/region/r.1.0.mca");
        var facts = WorldLayoutReader.Read(source.Root, source.Path("world"));
        Assert.HasCount(2, facts.Dimensions);
        Assert.AreEqual(4, facts.Dimensions.Sum(d => d.RegionFiles.Count));
        var preview = WorldScanner.Scan(source.Root, source.Path("world"));
        Assert.AreEqual(2, preview.Dimensions.Sum(d => d.Regions.Count));
        var limited = WorldLayoutReader.Read(source.Path("world"), source.Path("world"));
        Assert.IsFalse(limited.Dimensions.Any(d => d.Id == MinecraftPathGrammar.Nether));
        Assert.IsTrue(limited.Issues.Any(i => i.Code == "dimension_outside_source"));
    }

    [TestMethod]
    public void AmbiguityRemainsAnExplicitFactAndPreviewRejectsIt()
    {
        using var source = new Tree(); source.File("level.dat"); source.File("DIM-1/region/r.0.0.mca");
        source.File("dimensions/minecraft/the_nether/poi/r.0.0.mca");
        var facts = WorldLayoutReader.Read(source.Root, source.Root);
        Assert.IsTrue(facts.Issues.Any(i => i.Code == "dimension_layout_ambiguous" && i.DimensionId == MinecraftPathGrammar.Nether));
        Assert.ThrowsExactly<IOException>(() => WorldScanner.Scan(source.Root, source.Root));
    }

    [TestMethod]
    public void DisplayBudgetPreservesTheCompletedLowerBound()
    {
        using var source = new Tree(); source.File("level.dat");
        for (var index = 0; index < 8; index++) source.File($"region/r.{index}.0.mca");
        var facts = WorldLayoutReader.Read(source.Root, source.Root, false, new WorldScanBudget(4), default);
        Assert.IsFalse(facts.IsComplete);
        Assert.HasCount(4, facts.Dimensions.Single().RegionFiles);
        Assert.IsTrue(facts.Issues.Any(issue => issue.Code == "layout_read_incomplete"));
        Assert.ThrowsExactly<WorldScanLimitException>(() => WorldLayoutReader.Read(source.Root, source.Root, true, new WorldScanBudget(4), default));
    }

    [TestMethod]
    public void PartialInventoryDoesNotInventWorldIdentityOrTreatUnrelatedMcaAsRegions()
    {
        var facts = WorldLayoutReader.FromInventory(["region/r.0.0.mca", "unrelated/r.1.1.mca"]);
        Assert.AreEqual(".", facts.Single().WorldId);
        Assert.HasCount(1, facts.Single().Dimensions.Single().RegionFiles);
        Assert.HasCount(0, MinecraftPathGrammar.WorldRoots(["region/r.0.0.mca"]));
        Assert.IsTrue(MinecraftPathGrammar.TryExternalName("c.-33.64.mcc", out var chunk));
        Assert.AreEqual(new RegionKey(-2, 2), chunk.Region);
    }

    private sealed class Tree : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "layout-" + Guid.NewGuid().ToString("N"))).FullName;
        internal string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        internal void File(string relative) { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); System.IO.File.WriteAllBytes(path, []); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
