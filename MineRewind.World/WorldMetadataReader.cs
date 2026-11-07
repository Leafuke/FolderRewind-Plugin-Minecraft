using fNbt;

namespace MineRewind.World;

internal sealed record WorldMetadata(string Name, int? GameType, string Seed, long? Time, long? LastPlayed,
    bool HasPlayer, int? DataVersion, bool NewFormat);

internal static class WorldMetadataReader
{
    internal static WorldMetadata Read(string source, string world, CancellationToken token, Action<string> warning)
    {
        var inlinePlayer = false;
        using var input = Open(Path.Combine(world, "level.dat"));
        var file = BoundedNbtReader.Read(input, NbtReadLimits.Display, token, tag =>
        {
            if (tag.Name == "Player") { inlinePlayer = true; return false; }
            return tag.Name is null or "" or "Data" or "DataVersion" or "LevelName" or "GameType" or "Time"
                or "LastPlayed" or "RandomSeed" or "WorldGenSettings" or "seed" or "singleplayer_uuid";
        });
        var data = file.RootTag["Data"] as NbtCompound ?? throw new InvalidDataException("level.dat has no Data compound.");
        var modern = Directory.Exists(Path.Combine(world, "dimensions", "minecraft", "overworld")) || Directory.Exists(Path.Combine(world, "data", "minecraft"));
        var seed = ((data["RandomSeed"] as NbtLong)?.Value ?? ((data["WorldGenSettings"] as NbtCompound)?["seed"] as NbtLong)?.Value)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (modern)
        {
            seed = "";
            try
            {
                var path = Path.Combine(world, "data", "minecraft", "world_gen_settings.dat");
                if (File.Exists(path))
                {
                    using var settings = Open(path);
                    var root = BoundedNbtReader.Read(settings, NbtReadLimits.Display, token, t => t.Name is null or "" or "data" or "seed").RootTag;
                    seed = ((root["data"] as NbtCompound)?["seed"] as NbtLong)?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NbtFormatException) { warning("seed"); }
        }
        var player = inlinePlayer;
        if (modern && WorldNavigation.DecodeUuid(data["singleplayer_uuid"] as NbtIntArray) is { } uuid)
        {
            var path = Path.Combine(world, "players", "data", uuid + ".dat");
            if (File.Exists(path)) { WorldPaths.RequireContained(source, path); player = true; }
        }
        return new((data["LevelName"] as NbtString)?.Value ?? Path.GetFileName(world), (data["GameType"] as NbtInt)?.Value,
            seed, (data["Time"] as NbtLong)?.Value, (data["LastPlayed"] as NbtLong)?.Value,
            player, (data["DataVersion"] as NbtInt)?.Value, modern);
        FileStream Open(string path) => new(WorldPaths.RequireContained(source, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
}
