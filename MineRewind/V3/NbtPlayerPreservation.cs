using System.Buffers.Binary;
using FolderRewind.Plugin.Abstractions;
using fNbt;

namespace MineRewind;

/// <summary>All-player field overlay over stable views. Never accesses a live filesystem path.</summary>
internal static class NbtPlayerPreservation
{
    private static readonly string[] Fields =
    [ "Pos", "Rotation", "Dimension", "Inventory", "EnderItems", "XpLevel", "XpP", "XpTotal",
      "Score", "playerGameType", "Health", "foodLevel", "foodSaturationLevel" ];

    public static async Task<IReadOnlyList<RestoreStagedFileProposal>> PrepareAsync(
        IVersionMetadataSourceView currentView, IVersionMetadataSourceView targetView, CancellationToken token)
    {
        if (currentView is not IRestoreSourceView current || targetView is not IRestoreSourceView target)
            throw new InvalidDataException("Player preservation requires a locked file inventory.");
        var levelPath = await ResolveLevelPathAsync(current, token);
        if (!target.RelativePaths.Contains(levelPath, StringComparer.Ordinal))
            throw new InvalidDataException("Restore target does not contain the same world's level.dat.");
        var prefix = levelPath[..^"level.dat".Length];
        var currentLevel = await ReadAsync(current, levelPath, token);
        var targetLevel = await ReadAsync(target, levelPath, token);
        var currentData = RequireData(currentLevel);
        var targetData = RequireData(targetLevel);
        var currentNew = IdentifyLayout(currentData, current.RelativePaths, prefix);
        var targetNew = IdentifyLayout(targetData, target.RelativePaths, prefix);
        if (currentNew != targetNew) throw new InvalidDataException("Player preservation cannot cross the Minecraft 26.1 storage layout boundary.");
        var directory = prefix + (currentNew ? "players/data/" : "playerdata/");
        var currentPlayers = IndexPlayers(current.RelativePaths, directory);
        var targetPlayers = IndexPlayers(target.RelativePaths, directory);
        var proposals = new Dictionary<string, RestoreStagedFileProposal>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        NbtCompound? inline = null;
        string? inlineUuid = null;
        if (!currentNew && currentData["Player"] is { } inlineTag)
        {
            inline = inlineTag as NbtCompound ?? throw new InvalidDataException("Invalid embedded player compound.");
            inlineUuid = ReadUuid(inline);
            ValidateFields(inline);
        }
        var singleUuid = currentNew ? ReadSingleUuid(currentData) : inlineUuid;
        if (currentNew && singleUuid is not null && !currentPlayers.ContainsKey(singleUuid))
            throw new InvalidDataException("Current singleplayer_uuid does not reference an available player file.");
        var targetSingleUuid = currentNew ? ReadSingleUuid(targetData) : null;
        if (currentNew && targetSingleUuid is not null && !targetPlayers.ContainsKey(targetSingleUuid)
            && !currentPlayers.ContainsKey(targetSingleUuid))
            throw new InvalidDataException("Target singleplayer_uuid does not reference an available player file.");

        foreach (var entry in currentPlayers)
        {
            token.ThrowIfCancellationRequested();
            var source = await ReadAsync(current, entry.Value, token);
            ValidatePlayerIdentity(source.RootTag, entry.Key);
            var player = entry.Key == inlineUuid && inline is not null ? inline : source.RootTag;
            ValidateFields(player);
            NbtFile destination;
            var destinationPath = directory + entry.Key + ".dat";
            if (targetPlayers.TryGetValue(entry.Key, out var existingPath))
            {
                destinationPath = existingPath;
                destination = await ReadAsync(target, existingPath, token);
                ValidatePlayerIdentity(destination.RootTag, entry.Key);
                Overlay(destination.RootTag, player);
            }
            else destination = new NbtFile(ClonePlayerRoot(player));
            Add(destinationPath, destination);
        }

        var levelChanged = false;
        if (inline is not null)
        {
            var targetInline = targetData["Player"] as NbtCompound;
            if (targetData["Player"] is not null && targetInline is null)
                throw new InvalidDataException("Invalid target embedded player compound.");
            if (targetInline is null || (inlineUuid is not null && ReadUuid(targetInline) != inlineUuid))
            {
                Replace(targetData, inline);
                targetInline = (NbtCompound)targetData["Player"]!;
            }
            else Overlay(targetInline, inline);
            levelChanged = true;
            if (inlineUuid is not null)
            {
                // The embedded singleplayer state is authoritative for the same UUID.
                var path = directory + inlineUuid + ".dat";
                NbtFile file;
                if (targetPlayers.TryGetValue(inlineUuid, out var targetPath))
                {
                    path = targetPath;
                    file = await ReadAsync(target, path, token);
                    ValidatePlayerIdentity(file.RootTag, inlineUuid);
                    Overlay(file.RootTag, inline);
                }
                else file = new NbtFile(ClonePlayerRoot(inline));
                Add(path, file);
            }
        }
        if (currentNew && currentData["singleplayer_uuid"] is { } singleTag)
        {
            Replace(targetData, singleTag);
            levelChanged = true;
        }
        if (levelChanged) Add(levelPath, targetLevel);
        return proposals.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();

        void Add(string path, NbtFile file)
        {
            var content = file.SaveToBuffer(NbtCompression.GZip);
            if (proposals.TryGetValue(path, out var previous)) bytes -= previous.Content.Length;
            bytes = checked(bytes + content.Length);
            if (bytes > 64 * 1024 * 1024 || (!proposals.ContainsKey(path) && proposals.Count >= 4096))
                throw new InvalidDataException("Player preservation exceeds the proposal limit.");
            proposals[path] = new(path, content);
        }
    }

    private static async Task<string> ResolveLevelPathAsync(IRestoreSourceView view, CancellationToken token)
    {
        var paths = view.RelativePaths;
        if (paths.Contains("level.dat", StringComparer.Ordinal)) return "level.dat";
        if (paths.Contains("server.properties", StringComparer.Ordinal))
        {
            await using var stream = await view.OpenReadAsync("server.properties", token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!')) continue;
                var separator = trimmed.IndexOf('=');
                if (separator < 0 || !trimmed[..separator].Trim().Equals("level-name", StringComparison.OrdinalIgnoreCase)) continue;
                var name = trimmed[(separator + 1)..].Trim().Replace('\\', '/');
                if (name.Length == 0) break;
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part is ".." or "." or ""))
                    throw new InvalidDataException("Unsafe server level-name.");
                var level = name + "/level.dat";
                return paths.Contains(level, StringComparer.Ordinal) ? level
                    : throw new InvalidDataException("Configured server world is outside the managed inventory.");
            }
        }
        if (paths.Contains("world/level.dat", StringComparer.Ordinal)) return "world/level.dat";
        var levels = paths.Where(path => path.EndsWith("/level.dat", StringComparison.Ordinal)).Take(2).ToArray();
        return levels.Length == 1 ? levels[0] : throw new InvalidDataException("World root cannot be uniquely resolved from the managed inventory.");
    }

    private static bool IdentifyLayout(NbtCompound data, IReadOnlyList<string> paths, string prefix)
    {
        var legacy = paths.Any(path => path.StartsWith(prefix + "playerdata/", StringComparison.Ordinal));
        var modern = paths.Any(path => path.StartsWith(prefix + "players/data/", StringComparison.Ordinal));
        var embedded = data["Player"] is not null;
        var reference = data["singleplayer_uuid"] is not null;
        if (data["DataVersion"] is { } dataVersionTag && dataVersionTag is not NbtInt)
            throw new InvalidDataException("Invalid world DataVersion.");
        var versionName = (data["Version"] as NbtCompound)?["Name"] as NbtString;
        var namedModern = (data["DataVersion"] is NbtInt version && version.Value >= 4786)
            || (versionName is not null && int.TryParse(versionName.Value.Split('.')[0], out var major) && major >= 26);
        var knownLegacy = (data["DataVersion"] is NbtInt legacyVersion && legacyVersion.Value <= 4671)
            || (versionName?.Value.StartsWith("1.", StringComparison.Ordinal) == true);
        if (knownLegacy && (modern || reference || namedModern))
            throw new InvalidDataException("Player storage layout conflicts with the world's Minecraft version.");
        if ((legacy || embedded) && (modern || reference || namedModern))
            throw new InvalidDataException("Conflicting Minecraft player storage layout evidence.");
        return modern || reference || namedModern;
    }

    private static Dictionary<string, string> IndexPlayers(IReadOnlyList<string> paths, string directory)
    {
        var players = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in paths.Where(path => path.StartsWith(directory, StringComparison.Ordinal)))
        {
            var name = path[directory.Length..];
            if (name.Contains('/') || !name.EndsWith(".dat", StringComparison.Ordinal)) continue;
            if (!Guid.TryParseExact(name[..^4], "D", out var uuid)) throw new InvalidDataException("Invalid player file UUID.");
            if (!players.TryAdd(uuid.ToString("D"), path)) throw new InvalidDataException("Duplicate player file UUID.");
        }
        return players;
    }

    private static async Task<NbtFile> ReadAsync(IVersionMetadataSourceView view, string path, CancellationToken token)
    {
        await using var input = await view.OpenReadAsync(path, token).ConfigureAwait(false);
        var file = new NbtFile(); file.LoadFromStream(input, NbtCompression.AutoDetect); return file;
    }
    private static NbtCompound RequireData(NbtFile file) => file.RootTag["Data"] as NbtCompound
        ?? throw new InvalidDataException("World level.dat has no Data compound.");
    private static void Overlay(NbtCompound target, NbtCompound current)
    {
        foreach (var name in Fields) if (current[name] is { } tag) Replace(target, tag);
    }
    private static void Replace(NbtCompound compound, NbtTag tag)
    {
        if (compound.Contains(tag.Name!)) compound.Remove(tag.Name!);
        compound.Add((NbtTag)tag.Clone());
    }
    private static NbtCompound ClonePlayerRoot(NbtCompound player)
    {
        var clone = (NbtCompound)player.Clone(); clone.Name = ""; return clone;
    }
    private static void ValidateFields(NbtCompound player)
    {
        foreach (var name in Fields)
        {
            if (player[name] is not { } tag) continue;
            bool valid = name switch
            {
                "Pos" => tag is NbtList { Count: 3, ListType: NbtTagType.Double },
                "Rotation" => tag is NbtList { Count: 2, ListType: NbtTagType.Float },
                "Dimension" => tag is NbtString or NbtInt,
                "Inventory" or "EnderItems" => tag is NbtList list && (list.ListType is NbtTagType.Compound or NbtTagType.End),
                "Health" => tag is NbtFloat or NbtShort,
                "XpP" or "foodSaturationLevel" => tag is NbtFloat,
                _ => tag is NbtInt
            };
            if (!valid) throw new InvalidDataException($"Invalid player field '{name}'.");
        }
    }
    private static void ValidatePlayerIdentity(NbtCompound player, string expected)
    {
        var identity = ReadUuid(player);
        if (identity is not null && identity != expected) throw new InvalidDataException("Player NBT UUID differs from its filename.");
    }
    private static string? ReadSingleUuid(NbtCompound data)
        => data["singleplayer_uuid"] is { } tag ? DecodeUuid(tag) : null;
    private static string? ReadUuid(NbtCompound player)
    {
        if (player["UUID"] is { } tag) return DecodeUuid(tag);
        if (player["UUIDMost"] is null && player["UUIDLeast"] is null) return null;
        if (player["UUIDMost"] is not NbtLong most || player["UUIDLeast"] is not NbtLong least)
            throw new InvalidDataException("Invalid legacy player UUID.");
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, most.Value); BinaryPrimitives.WriteInt64BigEndian(bytes[8..], least.Value);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }
    private static string DecodeUuid(NbtTag tag)
    {
        if (tag is not NbtIntArray array || array.Value.Length != 4) throw new InvalidDataException("Invalid UUID int-array.");
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < 4; index++) BinaryPrimitives.WriteInt32BigEndian(bytes[(index * 4)..], array.Value[index]);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }
}
