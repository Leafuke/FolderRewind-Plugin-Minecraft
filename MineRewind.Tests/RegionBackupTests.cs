namespace MineRewind.Tests;

[TestClass]
public sealed class RegionBackupTests
{
    private readonly List<string> _temporaryRoots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (string root in _temporaryRoots.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LegacyOverworldIncludesMcaAndAllExternalChunkPatterns()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        bool success = Build(
            world,
            world,
            Parameters(("areas", "0,0,511,511")),
            out var rules,
            out _);

        Assert.IsTrue(success);
        CollectionAssert.Contains(rules.ToList(), "region/r.0.0.mca");
        CollectionAssert.Contains(rules.ToList(), "entities/r.0.0.mca");
        CollectionAssert.Contains(rules.ToList(), "poi/r.0.0.mca");
        CollectionAssert.Contains(rules.ToList(), "region/c.*.*.mcc");
        CollectionAssert.Contains(rules.ToList(), "entities/c.*.*.mcc");
        CollectionAssert.Contains(rules.ToList(), "poi/c.*.*.mcc");
    }

    [TestMethod]
    public void NegativeBlockCoordinateUsesNegativeRegion()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        bool success = Build(
            world,
            world,
            Parameters(("areas", "-1,-1,-1,-1")),
            out var rules,
            out _);

        Assert.IsTrue(success);
        CollectionAssert.Contains(rules.ToList(), "region/r.-1.-1.mca");
    }

    [TestMethod]
    public void InvalidOrNonFiniteAreaRejectsEntireScope()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        foreach (string area in new[] { "bad", "NaN,0,1,1", "Infinity,0,1,1", "30000001,0,1,1" })
        {
            bool success = Build(world, world, Parameters(("areas", area)), out _, out string errorCode);
            Assert.IsFalse(success, area);
            Assert.AreEqual("invalid_region_area", errorCode, area);
        }
    }

    [TestMethod]
    public void RegionLimitAccepts4096AndRejects4097()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        Assert.IsTrue(Build(
            world,
            world,
            Parameters(("areas", "0,0,32767,32767")),
            out _,
            out _));

        Assert.IsFalse(Build(
            world,
            world,
            Parameters(("areas", "0,0,2097663,0")),
            out _,
            out string errorCode));
        Assert.AreEqual("region_limit_exceeded", errorCode);
    }

    [TestMethod]
    public void DuplicateAreasAreDeduplicated()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        Assert.IsTrue(Build(
            world,
            world,
            Parameters(("areas", "0,0,511,511\n0,0,511,511")),
            out var rules,
            out _));
        Assert.AreEqual(1, rules.Count(rule => rule == "region/r.0.0.mca"));
    }

    [TestMethod]
    public void Post26LayoutUsesDimensionsDirectory()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "dimensions", "minecraft", "overworld"));

        Assert.IsTrue(Build(
            world,
            world,
            Parameters(("areas", "0,0,1,1")),
            out var rules,
            out _));
        CollectionAssert.Contains(
            rules.ToList(),
            "dimensions/minecraft/overworld/region/r.0.0.mca");
    }

    [TestMethod]
    public void PaperServerRootIncludesSiblingNether()
    {
        string server = CreateRoot();
        string world = Path.Combine(server, "world");
        Directory.CreateDirectory(Path.Combine(world, "region"));
        Directory.CreateDirectory(Path.Combine(server, "world_nether", "DIM-1"));

        var parameters = Parameters(
            ("dimensions", "overworld,nether"),
            ("areas", "0,0,1,1"));
        Assert.IsTrue(Build(server, world, parameters, out var rules, out _));
        CollectionAssert.Contains(
            rules.ToList(),
            "world_nether/DIM-1/region/r.0.0.mca");
    }

    [TestMethod]
    public void PaperSiblingOutsideSelectedWorldIsRejected()
    {
        string server = CreateRoot();
        string world = Path.Combine(server, "world");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(Path.Combine(server, "world_nether", "DIM-1"));

        bool success = Build(
            world,
            world,
            Parameters(("dimensions", "nether"), ("areas", "0,0,1,1")),
            out _,
            out string errorCode);

        Assert.IsFalse(success);
        Assert.AreEqual("dimension_outside_source", errorCode);
    }

    [TestMethod]
    public void AmbiguousAndMixedLayoutsAreRejected()
    {
        string ambiguousWorld = CreateWorld();
        Directory.CreateDirectory(Path.Combine(ambiguousWorld, "region"));
        Directory.CreateDirectory(Path.Combine(ambiguousWorld, "dimensions", "minecraft", "overworld"));
        Assert.IsFalse(Build(
            ambiguousWorld,
            ambiguousWorld,
            Parameters(("areas", "0,0,1,1")),
            out _,
            out string ambiguousCode));
        Assert.AreEqual("dimension_layout_ambiguous", ambiguousCode);

        string mixedWorld = CreateWorld();
        Directory.CreateDirectory(Path.Combine(mixedWorld, "region"));
        Directory.CreateDirectory(Path.Combine(mixedWorld, "dimensions", "minecraft", "the_nether"));
        Assert.IsFalse(Build(
            mixedWorld,
            mixedWorld,
            Parameters(("dimensions", "overworld,nether"), ("areas", "0,0,1,1")),
            out _,
            out string mixedCode));
        Assert.AreEqual("dimension_layout_mixed", mixedCode);
    }

    [TestMethod]
    public void MissingOrInvalidDimensionSelectionFailsClosed()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));

        Assert.IsFalse(Build(
            world,
            world,
            Parameters(
                ("dimension.overworld", "false"),
                ("dimension.nether", "false"),
                ("dimension.end", "false"),
                ("areas", "0,0,1,1")),
            out _,
            out string requiredCode));
        Assert.AreEqual("dimension_required", requiredCode);

        Assert.IsFalse(Build(
            world,
            world,
            Parameters(("dimension.overworld", "maybe"), ("areas", "0,0,1,1")),
            out _,
            out string flagCode));
        Assert.AreEqual("invalid_dimension_flag", flagCode);
    }

    [TestMethod]
    public void AreaLineAndByteLimitsAreEnforced()
    {
        string world = CreateWorld();
        Directory.CreateDirectory(Path.Combine(world, "region"));
        string tooManyLines = string.Join('\n', Enumerable.Repeat("0,0,1,1", 129));
        Assert.IsFalse(Build(
            world,
            world,
            Parameters(("areas", tooManyLines)),
            out _,
            out string lineCode));
        Assert.AreEqual("region_too_many_lines", lineCode);

        string tooLarge = new(' ', MinecraftSavesPlugin.MaxRegionAreaBytes + 1);
        Assert.IsFalse(Build(
            world,
            world,
            Parameters(("areas", tooLarge)),
            out _,
            out string sizeCode));
        Assert.AreEqual("region_input_too_large", sizeCode);
    }

    private static bool Build(
        string source,
        string world,
        IReadOnlyDictionary<string, string> parameters,
        out IReadOnlyList<string> rules,
        out string errorCode)
        => MinecraftSavesPlugin.TryBuildRegionBackupWhitelist(
            source,
            world,
            parameters,
            out rules,
            out errorCode,
            out _);

    private string CreateWorld()
        => CreateRoot();

    private string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MineRewindTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);
        return root;
    }

    private static IReadOnlyDictionary<string, string> Parameters(
        params (string Key, string Value)[] values)
        => values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}
