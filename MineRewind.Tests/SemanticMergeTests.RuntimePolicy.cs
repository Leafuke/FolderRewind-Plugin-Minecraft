using fNbt;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class SemanticMergeTests
{
    [TestMethod]
    [DataRow("conservative")]
    [DataRow("terrain-first")]
    public async Task EqualContentKeepsExactTargetRuntimePayload(string policy)
    {
        var b = RuntimeWorld("minecraft:stone", 1);
        var o = RuntimeWorld("minecraft:stone", 2);
        var t = RuntimeWorld("minecraft:stone", 3);
        var run = await Analyze(b, o, t, policy);
        Assert.IsTrue(run.Plan.Subjects.Single().IsAutomatic);
        Assert.AreEqual(1, run.Plan.Subjects.Single().State.GetProperty("AutomaticSide").GetInt32());
        var result = await run.Build();
        CollectionAssert.AreEqual(o["region/r.0.0.mca"], result["region/r.0.0.mca"],
            "Target pending updates, counters and caches must remain byte-for-byte intact.");
    }
    [TestMethod]
    public async Task SourceContentWinsOverTargetTimingAndCarriesItsOwnRuntimeState()
    {
        var b = RuntimeWorld("minecraft:stone", 1);
        var o = RuntimeWorld("minecraft:stone", 2);
        var t = RuntimeWorld("minecraft:dirt", 3);
        var run = await Analyze(b, o, t, "terrain-first");
        Assert.IsTrue(run.Plan.Subjects.Single().IsAutomatic);
        Assert.AreEqual(2, run.Plan.Subjects.Single().State.GetProperty("AutomaticSide").GetInt32());
        CollectionAssert.AreEqual(t["region/r.0.0.mca"], (await run.Build())["region/r.0.0.mca"]);
    }
    [TestMethod]
    public async Task ContentDisagreementUnknownModFieldsAndUnsupportedVersionsStayConflicts()
    {
        foreach (var variant in new[] { "blocks", "mod", "status", "version", "cache-extension" })
        {
            var inputs = Enumerable.Range(1, 3).Select(i => RuntimeWorld(
                variant == "blocks" ? new[] { "minecraft:stone", "minecraft:dirt", "minecraft:sand" }[i - 1] : "minecraft:stone", i,
                root =>
                {
                    if (variant == "mod") root.Add(new NbtCompound("neoforge:custom") { new NbtInt("energy", i) });
                    if (variant == "status") root.Add(new NbtString("Status", "generation-stage-" + i));
                    if (variant == "version") root["DataVersion"] = new NbtInt("DataVersion", int.MaxValue);
                    if (variant == "cache-extension") ((NbtCompound)root["Heightmaps"]!).Add(new NbtLongArray("mod:height", [(long)i]));
                })).ToArray();
            var run = await Analyze(inputs[0], inputs[1], inputs[2]);
            Assert.IsFalse(run.Plan.Subjects.Single().IsAutomatic, variant);
        }
    }
    [TestMethod]
    public async Task PoiDifferencesStillParticipateInContentDecisions()
    {
        var inputs = Enumerable.Range(1, 3).Select(i =>
        {
            var world = RuntimeWorld("minecraft:stone", i);
            world.Add("poi/r.0.0.mca", Region((0, Nbt(0, 0, i))));
            return world;
        }).ToArray();
        var run = await Analyze(inputs[0], inputs[1], inputs[2], "terrain-first");
        Assert.IsFalse(run.Plan.Subjects.Single().IsAutomatic);
    }
    private static Dictionary<string, byte[]> RuntimeWorld(string block, int runtime, Action<NbtCompound>? amend = null)
    {
        var root = new NbtCompound("")
        {
            new NbtInt("DataVersion", 3955), new NbtInt("xPos", 0), new NbtInt("zPos", 0),
            new NbtLong("LastUpdate", runtime * 100L), new NbtLong("InhabitedTime", runtime * 20L),
            new NbtByte("isLightOn", (byte)(runtime % 2)),
            new NbtCompound("Heightmaps") { new NbtLongArray("WORLD_SURFACE", Enumerable.Repeat((long)runtime, 36).ToArray()) },
            new NbtList("block_ticks", [new NbtCompound { new NbtString("i", "minecraft:stone"), new NbtInt("t", runtime),
                new NbtInt("p", 0), new NbtInt("x", 0), new NbtInt("y", 0), new NbtInt("z", 0) }]),
            new NbtList("fluid_ticks", NbtTagType.Compound),
            new NbtList("sections", [new NbtCompound
            {
                new NbtByte("Y", 0), new NbtByteArray("SkyLight", Enumerable.Repeat((byte)runtime, 2048).ToArray()),
                new NbtCompound("block_states") { new NbtList("palette", [new NbtCompound { new NbtString("Name", block) }]) },
                new NbtCompound("biomes") { new NbtList("palette", [new NbtString(null, "minecraft:plains")]) }
            }])
        };
        amend?.Invoke(root);
        return World(("region/r.0.0.mca", Region((0, new NbtFile(root).SaveToBuffer(NbtCompression.None)))));
    }
}
