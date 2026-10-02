using System.Buffers.Binary;
using FolderRewind.Plugin.Abstractions;
using fNbt;

namespace MineRewind.Tests;

[TestClass]
public sealed class PlayerPreservationTests
{
    private static readonly Guid A = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid B = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid C = Guid.Parse("20112233-4455-6677-8899-aabbccddeeff");

    [TestMethod]
    [DataRow(false, "")]
    [DataRow(true, "")]
    [DataRow(false, "custom-world/")]
    [DataRow(true, "custom-world/")]
    public async Task AllPlayersOverlaySelectedFieldsAndRetainPlayersAbsentFromBackup(bool modern, string prefix)
    {
        var current = World(modern, "Current", prefix);
        var target = World(modern, "Backup", prefix);
        var dir = prefix + (modern ? "players/data/" : "playerdata/");
        current.Add(dir + A + ".dat", Player(A, 42, 99));
        current.Add(dir + B + ".dat", Player(B, 43, 98));
        var backupPlayer = Player(A, 2, 7);
        foreach (var tag in backupPlayer)
        {
            if (tag.Name == "XpSeed") continue;
            if (tag is NbtInt integer) integer.Value = -1;
            if (tag is NbtFloat number) number.Value = -1;
            if (tag is NbtString text) text.Value = "minecraft:overworld";
        }
        ((NbtDouble)((NbtList)backupPlayer["Pos"]!)[0]).Value = -1;
        ((NbtFloat)((NbtList)backupPlayer["Rotation"]!)[0]).Value = -1;
        ((NbtList)backupPlayer["Inventory"]!).Add(new NbtCompound((string?)null) { new NbtString("id", "minecraft:stone") });
        ((NbtList)backupPlayer["EnderItems"]!).Add(new NbtCompound((string?)null) { new NbtString("id", "minecraft:stone") });
        target.Add(dir + A + ".dat", backupPlayer);
        target.Add(dir + C + ".dat", Player(C, 3, 8));
        current.Files[dir + A + ".dat_old"] = [1, 2, 3]; // Not another player; not a fallback.
        var advancements = prefix + (modern ? "players/advancements/" : "advancements/") + A + ".json";
        var stats = prefix + (modern ? "players/stats/" : "stats/") + A + ".json";
        current.Files[advancements] = System.Text.Encoding.UTF8.GetBytes("{\"current\":true}");
        target.Files[advancements] = System.Text.Encoding.UTF8.GetBytes("{\"backup\":true}");
        current.Files[stats] = System.Text.Encoding.UTF8.GetBytes("{\"current\":true}");
        target.Files[stats] = System.Text.Encoding.UTF8.GetBytes("{\"backup\":true}");
        var before = target.Files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var result = await NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None);
        Assert.HasCount(2, result);
        var shared = Read(result.Single(file => file.RelativePath == dir + A + ".dat").Content).RootTag;
        Assert.AreEqual(42, ((NbtInt)shared["XpLevel"]!).Value);
        Assert.AreEqual(7, ((NbtInt)shared["XpSeed"]!).Value);
        Assert.AreEqual(19.5f, ((NbtFloat)shared["Health"]!).Value);
        Assert.AreEqual("minecraft:the_nether", ((NbtString)shared["Dimension"]!).Value);
        Assert.AreEqual(5, ((NbtInt)shared["foodLevel"]!).Value);
        Assert.AreEqual(123, ((NbtInt)shared["XpTotal"]!).Value);
        Assert.AreEqual(0.5f, ((NbtFloat)shared["XpP"]!).Value);
        Assert.AreEqual(12, ((NbtInt)shared["Score"]!).Value);
        Assert.AreEqual(1, ((NbtInt)shared["playerGameType"]!).Value);
        Assert.AreEqual(0.7f, ((NbtFloat)shared["foodSaturationLevel"]!).Value);
        Assert.AreEqual(1d, ((NbtDouble)((NbtList)shared["Pos"]!)[0]).Value);
        Assert.AreEqual(90f, ((NbtFloat)((NbtList)shared["Rotation"]!)[0]).Value);
        Assert.HasCount(0, (NbtList)shared["Inventory"]!);
        Assert.HasCount(0, (NbtList)shared["EnderItems"]!);
        var added = Read(result.Single(file => file.RelativePath == dir + B + ".dat").Content).RootTag;
        Assert.AreEqual(98, ((NbtInt)added["XpSeed"]!).Value);
        Assert.IsFalse(result.Any(file => file.RelativePath == dir + C + ".dat"));
        Assert.IsFalse(result.Any(file => file.RelativePath.Contains("advancements") || file.RelativePath.Contains("stats")));
        foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, target.Files[pair.Key]);
    }

    [TestMethod]
    public async Task CurrentPlayerRepairsItsMissingBackupFileEvenWhenBackupReferencesThatUuid()
    {
        var current = World(true, "Current", single: A); var target = World(true, "Backup", single: A);
        current.Add($"players/data/{A}.dat", Player(A, 88, 99));
        var files = await NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None);
        Assert.AreEqual(88, ((NbtInt)Read(files.Single(file => file.RelativePath.EndsWith(A + ".dat")).Content).RootTag["XpLevel"]!).Value);
    }

    [TestMethod]
    public async Task ServerLevelNameResolvesMainWorldWhenDimensionWorldsHaveTheirOwnLevelDat()
    {
        var current = World(false, "Current", "custom/"); var target = World(false, "Backup", "custom/");
        current.Files["server.properties"] = System.Text.Encoding.UTF8.GetBytes("level-name=custom\n");
        current.Add("custom_nether/level.dat", new NbtCompound("") { new NbtCompound("Data") });
        current.Add($"custom/playerdata/{A}.dat", Player(A, 88, 99));
        target.Add($"custom/playerdata/{A}.dat", Player(A, 1, 2));
        var files = await NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None);
        Assert.AreEqual($"custom/playerdata/{A}.dat", files.Single().RelativePath);
    }

    [TestMethod]
    public async Task EmbeddedSingleplayerIsAuthoritativeForItsUuidAndOtherWorldFieldsRestore()
    {
        var current = World(false, "Current"); var target = World(false, "Backup");
        var inline = Player(A, 88, 100); inline.Name = "Player";
        current.Add("level.dat", new NbtCompound("") { new NbtCompound("Data") { new NbtString("LevelName", "Current"), inline } });
        var targetInline = Player(A, 1, 3); targetInline.Name = "Player";
        target.Add("level.dat", new NbtCompound("") { new NbtCompound("Data") { new NbtString("LevelName", "Backup"), targetInline } });
        current.Add($"playerdata/{A}.dat", Player(A, 9, 99));
        target.Add($"playerdata/{A}.dat", Player(A, 2, 4));
        var files = await NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None);
        Assert.HasCount(2, files);
        var data = (NbtCompound)Read(files.Single(file => file.RelativePath == "level.dat").Content).RootTag["Data"]!;
        Assert.AreEqual("Backup", ((NbtString)data["LevelName"]!).Value);
        Assert.AreEqual(88, ((NbtInt)((NbtCompound)data["Player"]!)["XpLevel"]!).Value);
        Assert.AreEqual(3, ((NbtInt)((NbtCompound)data["Player"]!)["XpSeed"]!).Value);
        Assert.AreEqual(88, ((NbtInt)Read(files.Single(file => file.RelativePath.Contains("playerdata")).Content).RootTag["XpLevel"]!).Value);
    }

    [TestMethod]
    public async Task ModernSingleplayerReferenceUsesCurrentUuidWithoutMappingItToTheBackupOwner()
    {
        var current = World(true, "Current", single: A); var target = World(true, "Backup", single: B);
        current.Add($"players/data/{A}.dat", Player(A, 88, 99));
        target.Add($"players/data/{B}.dat", Player(B, 1, 7));
        var files = await NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None);
        var data = (NbtCompound)Read(files.Single(file => file.RelativePath == "level.dat").Content).RootTag["Data"]!;
        CollectionAssert.AreEqual(Uuid("singleplayer_uuid", A).Value, ((NbtIntArray)data["singleplayer_uuid"]!).Value);
        Assert.AreEqual("Backup", ((NbtString)data["LevelName"]!).Value);
        Assert.IsTrue(files.Any(file => file.RelativePath == $"players/data/{A}.dat"));
    }

    [TestMethod]
    public async Task CorruptOrConflictingPlayersFailTheWholePreparationWithoutTouchingInputs()
    {
        var current = World(false, "Current"); var target = World(false, "Backup");
        current.Files[$"playerdata/{A}.dat"] = [1, 2, 3];
        await Assert.ThrowsAsync<Exception>(() => NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None));
        current.Add($"playerdata/{A}.dat", Player(B, 1, 1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None));
        Assert.HasCount(1, target.RelativePaths);
    }

    [TestMethod]
    public async Task CrossLayoutMixedLayoutAndMissingSingleplayerFileAreBlocked()
    {
        var old = World(false, "Old"); var modern = World(true, "New");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(old, modern, CancellationToken.None));
        old.Add($"players/data/{A}.dat", Player(A, 1, 1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(old, old, CancellationToken.None));
        modern.Add($"playerdata/{A}.dat", Player(A, 1, 1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(modern, modern, CancellationToken.None));
        var missing = World(true, "New", single: A);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(missing, World(true, "Backup"), CancellationToken.None));
    }

    [TestMethod]
    public async Task ProposalLimitDoesNotTruncatePlayers()
    {
        var current = World(false, "Current"); var target = World(false, "Target");
        for (var index = 0; index < 4097; index++)
        {
            var uuid = new Guid(index, 0, 0, new byte[8]);
            current.Add($"playerdata/{uuid}.dat", Player(uuid, 1, 1));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NbtHelper.PreparePlayerDataAsync(current, target, CancellationToken.None));
        Assert.HasCount(1, target.RelativePaths);
    }

    private static MemoryView World(bool modern, string name, string prefix = "", Guid? single = null)
    {
        var view = new MemoryView();
        var data = new NbtCompound("Data") { new NbtString("LevelName", name), new NbtInt("DataVersion", modern ? 4786 : 4671) };
        if (single.HasValue) data.Add(Uuid("singleplayer_uuid", single.Value));
        view.Add(prefix + "level.dat", new NbtCompound("") { data });
        return view;
    }
    private static NbtCompound Player(Guid uuid, int xp, int unselected) => new("")
    {
        Uuid("UUID", uuid), new NbtInt("XpLevel", xp), new NbtInt("XpSeed", unselected),
        new NbtInt("XpTotal", 123), new NbtFloat("XpP", 0.5f), new NbtInt("Score", 12),
        new NbtInt("playerGameType", 1), new NbtFloat("foodSaturationLevel", 0.7f),
        new NbtFloat("Health", 19.5f), new NbtString("Dimension", "minecraft:the_nether"), new NbtInt("foodLevel", 5),
        new NbtList("Pos", [new NbtDouble(null, 1), new NbtDouble(null, 64), new NbtDouble(null, 2)], NbtTagType.Double),
        new NbtList("Rotation", [new NbtFloat(null, 90), new NbtFloat(null, 0)], NbtTagType.Float),
        new NbtList("Inventory", NbtTagType.Compound), new NbtList("EnderItems", NbtTagType.Compound)
    };
    private static NbtIntArray Uuid(string name, Guid uuid)
    {
        var bytes = uuid.ToByteArray(bigEndian: true); var values = new int[4];
        for (var index = 0; index < 4; index++) values[index] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(index * 4));
        return new(name, values);
    }
    private static NbtFile Read(ReadOnlyMemory<byte> bytes)
    {
        var file = new NbtFile(); file.LoadFromStream(new MemoryStream(bytes.ToArray()), NbtCompression.AutoDetect); return file;
    }
    private sealed class MemoryView : IRestoreSourceView
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> RelativePaths => Files.Keys.Order(StringComparer.Ordinal).ToArray();
        public void Add(string path, NbtCompound root) => Files[path] = new NbtFile(root).SaveToBuffer(NbtCompression.GZip);
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(Files[relativePath], writable: false));
        }
    }
}
