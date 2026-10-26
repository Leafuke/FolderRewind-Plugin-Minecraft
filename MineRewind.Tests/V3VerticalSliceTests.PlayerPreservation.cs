using System.Buffers.Binary;
using fNbt;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlayerPreservationOverlaysFieldsAndKeepsTargetWorldAndUnrelatedPlayerData(bool modern)
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate(preservePlayerData: true);
        var (config, folder) = Snapshots(world.WorldPath);
        var playerId = Guid.NewGuid();
        var targetOnlyId = Guid.NewGuid();
        var directory = modern ? "players/data/" : "playerdata/";
        var playerPath = directory + playerId.ToString("D") + ".dat";
        var targetOnlyPath = directory + targetOnlyId.ToString("D") + ".dat";
        var current = new PlayerSourceView(new()
        {
            ["level.dat"] = Level("current-world", modern, playerId, 42),
            [playerPath] = Player(playerId, modern ? 42 : 3, "current-only-field")
        });
        var target = new PlayerSourceView(new()
        {
            ["level.dat"] = Level("target-world", modern, modern ? targetOnlyId : playerId, 1),
            [playerPath] = Player(playerId, 1, "target-only-field"),
            [targetOnlyPath] = Player(targetOnlyId, 9, "target-other-player")
        });
        var before = target.Files.ToDictionary(p => p.Key, p => p.Value.ToArray());
        var request = new RestoreStagingPreparationRequest(Guid.NewGuid(), config, folder.FolderId, current, target, true);
        var result = await fixture.Plugin.PrepareAsync(request, fixture.Invocation);
        Assert.IsEmpty(result.Diagnostics);
        CollectionAssert.AreEquivalent(new[] { "level.dat", playerPath }, result.Files.Select(f => f.RelativePath).ToArray());
        var player = Read(result.Files.Single(f => f.RelativePath == playerPath).Content).RootTag;
        Assert.AreEqual(42, player["XpLevel"]!.IntValue);
        Assert.AreEqual("target-only-field", player["Unrelated"]!.StringValue);
        Assert.AreEqual(3, ((NbtList)player["Pos"]!).Count);
        var level = (NbtCompound)Read(result.Files.Single(f => f.RelativePath == "level.dat").Content).RootTag["Data"]!;
        Assert.AreEqual("target-world", level["LevelName"]!.StringValue);
        if (modern) CollectionAssert.AreEqual(Uuid(playerId), level["singleplayer_uuid"]!.IntArrayValue);
        else Assert.AreEqual(42, ((NbtCompound)level["Player"]!)["XpLevel"]!.IntValue);
        foreach (var pair in before) CollectionAssert.AreEqual(pair.Value, target.Files[pair.Key]);
        Assert.AreEqual(0, current.ActiveReaders);
        Assert.AreEqual(0, target.ActiveReaders);
        var disabled = await fixture.Plugin.PrepareAsync(request with { PreservePlayerDataOverride = false }, fixture.Invocation);
        Assert.IsEmpty(disabled.Files);

        static byte[] Level(string name, bool modern, Guid id, int xp)
        {
            var data = new NbtCompound("Data") { new NbtInt("DataVersion", modern ? 4786 : 4321), new NbtString("LevelName", name) };
            if (modern) data.Add(new NbtIntArray("singleplayer_uuid", Uuid(id)));
            else { var player = Read(Player(id, xp, "inline")).RootTag; player.Name = "Player"; data.Add(player); }
            return new NbtFile(new NbtCompound("") { data }).SaveToBuffer(NbtCompression.GZip);
        }
        static byte[] Player(Guid id, int xp, string unrelated) => new NbtFile(new NbtCompound("")
        {
            new NbtIntArray("UUID", Uuid(id)), new NbtInt("XpLevel", xp), new NbtString("Unrelated", unrelated),
            new NbtList("Inventory", NbtTagType.Compound),
            new NbtList("Pos", NbtTagType.Double) { new NbtDouble(10), new NbtDouble(64), new NbtDouble(20) }
        }).SaveToBuffer(NbtCompression.GZip);
        static int[] Uuid(Guid id)
        {
            var bytes = id.ToByteArray(bigEndian: true);
            return Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4, 4))).ToArray();
        }
        static NbtFile Read(ReadOnlyMemory<byte> bytes)
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            var file = new NbtFile(); file.LoadFromStream(stream, NbtCompression.AutoDetect); return file;
        }
    }

    private sealed class PlayerSourceView(Dictionary<string, byte[]> files) : IRestoreSourceView
    {
        public Dictionary<string, byte[]> Files => files;
        public IReadOnlyList<string> RelativePaths { get; } = files.Keys.ToArray();
        public int ActiveReaders { get; private set; }
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ActiveReaders++;
            return ValueTask.FromResult<Stream>(new PlayerStream(files[relativePath], () => ActiveReaders--));
        }
        private sealed class PlayerStream(byte[] bytes, Action closed) : MemoryStream(bytes, false)
        {
            private bool _disposed;
            protected override void Dispose(bool disposing)
            { if (disposing && !_disposed) { _disposed = true; closed(); } base.Dispose(disposing); }
        }
    }
}
