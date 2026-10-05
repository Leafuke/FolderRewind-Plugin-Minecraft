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
            var complete = new bool[256];
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
                var materials = palette.Cast<NbtCompound>().Select(p => BlockColors.Material(version.Value,
                    p.Get<NbtString>("Name")?.Value ?? throw new InvalidDataException("Missing block name."), p.Get<NbtCompound>("Properties"))).ToArray();
                var biomes = section.Get<NbtCompound>("biomes");
                var biomePalette = biomes?.Get<NbtList>("palette")?.Cast<NbtString>().Select(b => b.Value).ToArray() ?? ["minecraft:plains"];
                if (biomePalette.Length is < 1 or > 64) throw new InvalidDataException("Invalid biome palette.");
                var biomeData = biomes?.Get<NbtLongArray>("data")?.Value;
                var biomeBits = Math.Max(1, (int)Math.Ceiling(Math.Log2(biomePalette.Length)));
                var biomePerLong = 64 / biomeBits;
                if (biomePalette.Length > 1 && (biomeData is null || biomeData.Length != (64 + biomePerLong - 1) / biomePerLong))
                    throw new InvalidDataException("Invalid biome array.");
                string Biome(int x, int y, int z)
                {
                    var i = (y / 4) * 16 + (z / 4) * 4 + x / 4;
                    var n = biomePalette.Length == 1 ? 0 : (int)((unchecked((ulong)biomeData![i / biomePerLong]) >> (i % biomePerLong * biomeBits)) & ((1UL << biomeBits) - 1));
                    return n < biomePalette.Length ? biomePalette[n] : throw new InvalidDataException("Invalid biome index.");
                }
                var bits = Math.Max(4, (int)Math.Ceiling(Math.Log2(materials.Length)));
                var perLong = 64 / bits;
                var data = states.Get<NbtLongArray>("data")?.Value;
                if (materials.Length > 1 && (data is null || data.Length != (4096 + perLong - 1) / perLong))
                    throw new InvalidDataException("Invalid block-state array.");
                for (var z = 0; z < 16; z++)
                for (var x = 0; x < 16; x++)
                {
                    var column = z * 16 + x;
                    if (complete[column]) continue;
                    for (var y = Math.Min(15, maximumY - sectionY * 16); y >= 0; y--)
                    {
                        var index = y * 256 + column;
                        var paletteIndex = materials.Length == 1 ? 0 : (int)((unchecked((ulong)data![index / perLong]) >> ((index % perLong) * bits)) & ((1UL << bits) - 1));
                        if (paletteIndex >= materials.Length) throw new InvalidDataException("Invalid palette index.");
                        var material = materials[paletteIndex];
                        if ((material.Flags & 1) != 0 || material.Name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air") continue;
                        var biome = Biome(x, y, z); var absoluteY = sectionY * 16 + y;
                        var color = BlockColors.Tint(version.Value, material, biome);
                        var water = (material.Flags & 8) != 0;
                        if (columns[column] is null)
                        {
                            columns[column] = new(material.Name, absoluteY, color, biome, material.Missing);
                            if (material.Waterlogged && !water)
                            {
                                var waterColor = BlockColors.Tint(version.Value, BlockColors.Material(version.Value, "minecraft:water", null), biome);
                                columns[column] = columns[column]! with { Argb = waterColor, WaterBottomHeight = absoluteY - 1, WaterBottomColor = color };
                                complete[column] = true; break;
                            }
                            if (!water) { complete[column] = true; break; }
                        }
                        else if (!water)
                        {
                            columns[column] = columns[column]! with { WaterBottomHeight = absoluteY, WaterBottomColor = color };
                            complete[column] = true; break;
                        }
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
