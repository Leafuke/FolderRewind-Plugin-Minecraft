using System.Buffers.Binary;
using System.Text.Json;
using fNbt;

namespace MineRewind.World;

public sealed record WorldNavigationTarget(string Id, string? Name, string Dimension, double X, double Z, bool SinglePlayer, bool Spawn)
{
    public int? Height { get; init; }
}

/// <summary>Read-only saved positions. Never loads inventories into a long-lived preview model.</summary>
public static class WorldNavigation
{
    public static string? DecodeUuid(NbtIntArray? value)
    {
        if (value?.Value.Length != 4) return null;
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt32BigEndian(bytes[(i * 4)..], value.Value[i]);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }
    public static IReadOnlyList<WorldNavigationTarget> Read(WorldDescriptor world, CancellationToken token, Action<string>? incomplete = null)
    {
        token.ThrowIfCancellationRequested();
        var budget = new WorldScanBudget();
        var names = ReadNames(world, token);
        var targets = new Dictionary<string, WorldNavigationTarget>(StringComparer.Ordinal);
        var data = TryRead(Path.Combine(world.WorldRoot, "level.dat"))?["Data"] as NbtCompound;
        var single = DecodeUuid(data?["singleplayer_uuid"] as NbtIntArray);
        if (data?["Player"] is NbtCompound inline)
        {
            single = DecodeUuid(inline["UUID"] as NbtIntArray) ?? "singleplayer";
            AddPlayer(single, inline, true);
        }
        if (single is not null && !targets.ContainsKey(single)
            && TryRead(Path.Combine(world.WorldRoot, "players", "data", single + ".dat")) is { } singlePlayer)
            AddPlayer(single, singlePlayer, true);
        var scanned = 0;
        foreach (var relative in new[] { "playerdata", Path.Combine("players", "data") })
        {
            var directory = Path.Combine(world.WorldRoot, relative);
            if (!Directory.Exists(directory)) continue;
            try
            {
                WorldPaths.RequireContained(world.SourceRoot, directory);
                foreach (var file in budget.Entries(world.SourceRoot, directory, token, rejectLinks: false))
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(file) || !file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) continue;
                    if (++scanned > 4094) { incomplete?.Invoke("players"); break; }
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id)) continue;
                    var key = id.ToString("D");
                    if (targets.ContainsKey(key)) continue; // Embedded singleplayer state is authoritative.
                    if (TryRead(file) is { } player) AddPlayer(key, player, key == single);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { incomplete?.Invoke("scan"); }
        }
        if (budget.Incomplete) incomplete?.Invoke("scan");
        if (data?.Get<NbtInt>("SpawnX") is { } sx && data.Get<NbtInt>("SpawnZ") is { } sz)
            targets["spawn"] = new("spawn", null, "minecraft:overworld", sx.Value, sz.Value, false, true);
        // Newer level.dat layouts use a structured global spawn record.
        else if (data?.Get<NbtCompound>("spawn") is { } spawn && spawn.Get<NbtIntArray>("pos")?.Value is { Length: 3 } pos)
            targets["spawn"] = new("spawn", null, spawn.Get<NbtString>("dimension")?.Value ?? "minecraft:overworld", pos[0], pos[2], false, true);
        return targets.Values.Where(t => double.IsFinite(t.X) && double.IsFinite(t.Z) && Math.Abs(t.X) <= 30_000_512 && Math.Abs(t.Z) <= 30_000_512).ToArray();

        void AddPlayer(string id, NbtCompound player, bool isSingle)
        {
            if (player["Pos"] is not NbtList { Count: 3, ListType: NbtTagType.Double } position) return;
            var dimension = player["Dimension"] switch
            {
                NbtString text => text.Value,
                NbtInt n => n.Value switch { -1 => "minecraft:the_nether", 0 => "minecraft:overworld", 1 => "minecraft:the_end", _ => "" },
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(dimension) || dimension.Length > 256) return;
            var name = player["bukkit"] is NbtCompound bukkit && bukkit["lastKnownName"] is NbtString knownName ? knownName.Value : null;
            if (name?.Length > 128) name = null;
            if (string.IsNullOrWhiteSpace(name)) names.TryGetValue(id, out name);
            var y = ((NbtDouble)position[1]).Value;
            targets[id] = new(id, name, dimension, ((NbtDouble)position[0]).Value, ((NbtDouble)position[2]).Value, isSingle, false)
                { Height = double.IsFinite(y) && y is >= -2048 and <= 2047 ? (int)Math.Floor(y) : null };
        }
        NbtCompound? TryRead(string path)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) return null;
                WorldPaths.RequireContained(world.SourceRoot, path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var nbt = BoundedNbtReader.Read(file, NbtReadLimits.Display, token,
                    tag => tag.Name is null or "" or "Data" or "Player" or "Pos" or "Dimension" or "UUID"
                        or "singleplayer_uuid" or "SpawnX" or "SpawnZ" or "spawn" or "pos" or "dimension" or "bukkit" or "lastKnownName");
                return nbt.RootTag;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NbtFormatException or InvalidCastException or ArgumentException)
            { incomplete?.Invoke("nbt"); return null; }
        }
    }
    private static Dictionary<string, string> ReadNames(WorldDescriptor world, CancellationToken token)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(world.SourceRoot, "usercache.json");
        try
        {
            if (!File.Exists(path)) return names;
            WorldPaths.RequireContained(world.SourceRoot, path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 2 * 1024 * 1024) return names;
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Array) return names;
            foreach (var entry in json.RootElement.EnumerateArray().Take(4096))
            {
                token.ThrowIfCancellationRequested();
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("uuid", out var id) && id.ValueKind == JsonValueKind.String
                    && Guid.TryParse(id.GetString(), out var uuid) && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 128 } text)
                    names[uuid.ToString("D")] = text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return names;
    }
}
