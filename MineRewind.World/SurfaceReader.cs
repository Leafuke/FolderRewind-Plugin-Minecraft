using fNbt;

namespace MineRewind.World;

public static class SurfaceReader
{
    // Release DataVersions, 1.18–26.1.2. No floor-entry fallback into future/snapshot formats.
    // Source: https://github.com/misode/mcmeta/tree/summary/versions (2026-10-05).
    private static readonly HashSet<int> SupportedVersions = [2860, 2865, 2975, 3105, 3117, 3120, 3218, 3337,
        3463, 3465, 3578, 3698, 3700, 3837, 3839, 3953, 3955, 4080, 4082, 4189, 4325, 4435,
        4438, 4440, 4554, 4556, 4671, 4786, 4788, 4790];

    public static ChunkSurface Read(ChunkKey key, ChunkRecord record, int maximumY, CancellationToken token = default)
    {
        if (record.State != ChunkReadState.Present || record.Nbt is null) return ChunkSurface.Empty(key, record.State);
        try
        {
            using var input = new MemoryStream(record.Nbt, writable: false);
            // Validate before constructing an object tree: fNbt itself does not enforce depth/allocation quotas.
            var reader = new NbtReader(input);
            while (reader.ReadToFollowing())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 64 || reader.TagsRead > 100_000 || (reader.HasLength && (reader.TagLength < 0 || reader.TagLength > 1_048_576)))
                    throw new InvalidDataException("NBT exceeds preview limits.");
            }
            input.Position = 0;
            var nbt = new NbtFile(); nbt.LoadFromStream(input, NbtCompression.None);
            var root = nbt.RootTag;
            var version = root.Get<NbtInt>("DataVersion")?.Value;
            if (version is null || !SupportedVersions.Contains(version.Value))
                return ChunkSurface.Empty(key, ChunkReadState.UnsupportedVersion, version);
            if (root.Get<NbtInt>("xPos")?.Value != key.X || root.Get<NbtInt>("zPos")?.Value != key.Z)
                return ChunkSurface.Empty(key, ChunkReadState.Corrupt, version);
            var sections = root.Get<NbtList>("sections") ?? throw new InvalidDataException("Missing sections.");
            var columns = new SurfaceColumn?[256];
            var seen = new HashSet<int>();
            foreach (var section in sections.Cast<NbtCompound>().OrderByDescending(s => unchecked((sbyte)(s.Get<NbtByte>("Y")?.Value ?? 0))))
            {
                token.ThrowIfCancellationRequested();
                var yTag = section.Get<NbtByte>("Y") ?? throw new InvalidDataException("Missing section Y.");
                var sectionY = unchecked((sbyte)yTag.Value);
                if (!seen.Add(sectionY)) throw new InvalidDataException("Duplicate section.");
                if (sectionY * 16 > maximumY) continue;
                var states = section.Get<NbtCompound>("block_states");
                if (states is null) continue; // Lighting-only section.
                var palette = states.Get<NbtList>("palette") ?? throw new InvalidDataException("Missing palette.");
                if (palette.Count is < 1 or > 4096) throw new InvalidDataException("Invalid palette length.");
                var names = palette.Cast<NbtCompound>().Select(p => p.Get<NbtString>("Name")?.Value
                    ?? throw new InvalidDataException("Missing block name.")).ToArray();
                var bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(names.Length)));
                var perLong = 64 / bits;
                var data = states.Get<NbtLongArray>("data")?.Value;
                if (names.Length > 1 && (data is null || data.Length != (4096 + perLong - 1) / perLong))
                    throw new InvalidDataException("Invalid block-state array.");
                for (var z = 0; z < 16; z++)
                for (var x = 0; x < 16; x++)
                {
                    var column = z * 16 + x;
                    if (columns[column] is not null) continue;
                    for (var y = Math.Min(15, maximumY - sectionY * 16); y >= 0; y--)
                    {
                        var index = y * 256 + column;
                        var paletteIndex = names.Length == 1 ? 0 : (int)((unchecked((ulong)data![index / perLong]) >> ((index % perLong) * bits)) & ((1UL << bits) - 1));
                        if (paletteIndex >= names.Length) throw new InvalidDataException("Invalid palette index.");
                        var name = names[paletteIndex];
                        if (name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air") continue;
                        columns[column] = new(name, sectionY * 16 + y, BlockColors.For(name));
                        break;
                    }
                }
            }
            return new(key, ChunkReadState.Present, version, Array.AsReadOnly(columns));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or NbtFormatException or InvalidCastException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return ChunkSurface.Empty(key, ChunkReadState.Corrupt);
        }
    }
}
