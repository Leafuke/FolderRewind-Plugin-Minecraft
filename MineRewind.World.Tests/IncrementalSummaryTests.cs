using System.Text.Json;
using MineRewind.World.Semantic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MineRewind.World.Tests;

public sealed partial class IncrementalTests
{
    [TestMethod]
    public void MissingExternalWarningSurvivesUnchangedCaptureAndClearsAfterRepair()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["level.dat"] = [1], ["region/r.0.0.mca"] = Region(external: true), ["region/c.0.0.mcc"] = Nbt(1) }), objects);
        var missing = Capture(new(new()), objects, first, ["region/c.0.0.mcc"]);
        Assert.AreEqual(1, missing.Files.Single(f => f.Index is not null).SemanticSummary!.MissingExternalFiles);
        objects.Reads.Clear();
        var unchanged = Capture(new(new()), objects, missing);
        Assert.IsEmpty(objects.Reads);
        Assert.AreEqual(1, unchanged.Files.Single(f => f.Index is not null).SemanticSummary!.MissingExternalFiles);
        var repaired = Capture(new(new() { ["region/c.0.0.mcc"] = Nbt(2) }), objects, unchanged);
        Assert.IsFalse(repaired.Files.Single(f => f.Index is not null).SemanticSummary!.HasWarnings);
        CollectionAssert.AreEqual(Region(external: true), Restore(repaired, "region/r.0.0.mca", objects));
    }
    [TestMethod]
    public void LegacyV1HydratesIndexesOnceWithoutChangingRecipesOrRawBytes()
    {
        var objects = new Objects();
        var original = Capture(new(new() { ["region/r.0.0.mca"] = Region() }), objects);
        var oldJson = JsonSerializer.SerializeToNode(original, SemanticJson.Options)!.AsObject();
        oldJson.Remove(nameof(WorldIncrementalState.SemanticClassifierVersion)); oldJson.Remove(nameof(WorldIncrementalState.WorldRootsFingerprint));
        foreach (var file in oldJson[nameof(WorldIncrementalState.Files)]!.AsArray()) file!.AsObject().Remove(nameof(WorldStateFile.SemanticSummary));
        var legacy = oldJson.Deserialize<WorldIncrementalState>(SemanticJson.Options)!;
        Assert.AreEqual(0, legacy.SemanticClassifierVersion); Assert.IsNull(legacy.Files[0].SemanticSummary);
        var hydrated = Capture(new(new()), objects, legacy);
        Assert.IsNotNull(hydrated.Files[0].SemanticSummary);
        Assert.AreEqual(legacy.Files[0].Recipe, hydrated.Files[0].Recipe);
        objects.Reads.Clear();
        var next = Capture(new(new()), objects, hydrated);
        Assert.IsEmpty(objects.Reads);
        CollectionAssert.AreEqual(Region(), Restore(next, "region/r.0.0.mca", objects));
        var reloaded = JsonSerializer.Deserialize<LegacyState>(JsonSerializer.Serialize(next, SemanticJson.Options), SemanticJson.Options)!;
        Assert.AreEqual(1, reloaded.Version);
        Assert.AreEqual(next.Files[0].Recipe, reloaded.Files[0].Recipe);
    }
    [TestMethod]
    public void WorldRootChangesReclassifyInheritedIndexWithoutRewritingRecipe()
    {
        var objects = new Objects();
        var first = Capture(new(new() { ["nested/region/r.0.0.mca"] = Region() }), objects);
        Assert.AreEqual(1, first.Files[0].SemanticSummary!.UnclassifiedRegions);
        var recipe = first.Files[0].Recipe;
        var next = Capture(new(new() { ["nested/level.dat"] = [1] }), objects, first);
        var region = next.Files.Single(f => f.Path.EndsWith(".mca"));
        Assert.AreEqual(recipe, region.Recipe);
        Assert.IsFalse(region.SemanticSummary!.HasWarnings);
        Assert.AreNotEqual(first.WorldRootsFingerprint, next.WorldRootsFingerprint);
        CollectionAssert.AreEqual(Region(), Restore(next, region.Path, objects));
    }
    [TestMethod]
    public void InvalidCachedSummaryIsRebuiltAndDeletingOwnerRemovesItsWarning()
    {
        var objects = new Objects(); var first = Capture(new(new() { ["region/r.0.0.mca"] = Region(external: true) }), objects);
        Assert.IsTrue(first.Files[0].SemanticSummary!.HasWarnings);
        var invalid = first with { Files = first.Files.Select(f => f with { SemanticSummary = new(1, -1, 0, 0, 0, 0) }).ToArray() };
        var refreshed = Capture(new(new()), objects, invalid);
        Assert.IsTrue(refreshed.Files[0].SemanticSummary!.IsValid); Assert.IsTrue(refreshed.Files[0].SemanticSummary!.HasWarnings);
        var removed = Capture(new(new()), objects, refreshed, ["region/r.0.0.mca"]);
        Assert.IsEmpty(removed.Files);
    }
    private sealed record LegacyFile(string Path, long Length, string Sha256, WorldObjectReference Recipe, WorldObjectReference? Index);
    private sealed record LegacyState(int Version, string SourceId, string BoundaryFingerprint, IReadOnlyList<LegacyFile> Files);
}
