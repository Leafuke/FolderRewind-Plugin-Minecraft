using fNbt;
using System.Security.Cryptography;
using System.Text;

namespace MineRewind.World;

/// <summary>Version-gated semantic comparison. Exact payload identities remain the write/verification authority.</summary>
public sealed record ChunkContentFingerprint(string Blocks, string BlockEntities, string Biomes, string Other)
{
    public string MergeIdentity { get; init; } = "";
    public static ChunkContentFingerprint? Read(byte[] bytes, ChunkKey key, CancellationToken token = default)
        => Read(bytes, bytes.Length, key, token);
    internal static ChunkContentFingerprint? Read(byte[] bytes, int length, ChunkKey key, CancellationToken token = default)
    {
        try
        {
            using var input = new MemoryStream(bytes, 0, length, false);
            var reader = new NbtReader(input);
            while (reader.ReadToFollowing())
            {
                token.ThrowIfCancellationRequested();
                if (reader.Depth > 64 || reader.TagsRead > 100_000 || reader.HasLength && (reader.TagLength < 0 || reader.TagLength > 1_048_576))
                    return null;
            }
            input.Position = 0;
            var file = new NbtFile(); file.LoadFromStream(input, NbtCompression.None);
            var root = file.RootTag;
            var version = root.Get<NbtInt>("DataVersion")?.Value;
            if (version is null || !SurfaceReader.SupportsVersion(version.Value) || root.Contains("Level")
                || root.Get<NbtInt>("xPos")?.Value != key.X || root.Get<NbtInt>("zPos")?.Value != key.Z
                || root.Get<NbtList>("sections") is not { } list || list.Count > 256) return null;
            var sections = list.Cast<NbtCompound>().OrderBy(s => unchecked((sbyte)(s.Get<NbtByte>("Y")?.Value ?? throw new InvalidDataException()))).ToArray();
            if (sections.Select(s => s.Get<NbtByte>("Y")!.Value).Distinct().Count() != sections.Length) return null;
            if (root.Contains("block_entities") && root["block_entities"] is not NbtList) return null;
            if (sections.Any(s => s.Contains("block_states") && s["block_states"] is not NbtCompound
                || s.Contains("biomes") && s["biomes"] is not NbtCompound)) return null;
            var blocks = Hash(w =>
            {
                foreach (var section in sections)
                {
                    token.ThrowIfCancellationRequested();
                    if (section.Get<NbtCompound>("block_states") is not { } states) continue;
                    var palette = states.Get<NbtList>("palette") ?? throw new InvalidDataException();
                    var names = palette.Select(p => BlockName(version.Value, p)).ToArray();
                    var indices = Indices(states, names.Length, 4096, 4);
                    if (indices.All(i => names[i] == "minecraft:air")) continue;
                    w.Write(unchecked((sbyte)section.Get<NbtByte>("Y")!.Value));
                    foreach (var i in indices) w.Write(names[i]);
                }
            });
            var biomes = Hash(w =>
            {
                foreach (var section in sections)
                {
                    token.ThrowIfCancellationRequested();
                    if (section.Get<NbtCompound>("biomes") is not { } states) continue;
                    var palette = states.Get<NbtList>("palette")?.Cast<NbtString>().Select(p => p.Value).ToArray() ?? throw new InvalidDataException();
                    w.Write(unchecked((sbyte)section.Get<NbtByte>("Y")!.Value));
                    foreach (var i in Indices(states, palette.Length, 64, 1)) w.Write(palette[i]);
                }
            });
            var blockEntities = Hash(w => { if (root["block_entities"] is { } value) WriteTag(w, value, token); });
            // Remove only the data represented above. Unknown fields remain visible in Other.
            string OtherHash(bool runtimeIndependent) => Hash(w =>
            {
                foreach (var tag in root.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (tag.Name == "block_entities") continue;
                    if (runtimeIndependent && IsKnownRuntimeField(tag)) continue;
                    if (tag.Name != "sections") { WriteTag(w, tag, token); continue; }
                    w.Write("sections");
                    foreach (var section in sections)
                    {
                        var remaining = section.Where(t => t.Name is not ("Y" or "block_states" or "biomes")
                            && !(runtimeIndependent && t is NbtByteArray { Value.Length: 2048 } && t.Name is "BlockLight" or "SkyLight")).ToArray();
                        var extraStates = section.Get<NbtCompound>("block_states")?.Where(t => t.Name is not ("palette" or "data")).ToArray() ?? [];
                        var extraBiomes = section.Get<NbtCompound>("biomes")?.Where(t => t.Name is not ("palette" or "data")).ToArray() ?? [];
                        if (remaining.Length + extraStates.Length + extraBiomes.Length == 0) continue;
                        w.Write(unchecked((sbyte)section.Get<NbtByte>("Y")!.Value));
                        foreach (var tag2 in remaining.OrderBy(t => t.Name, StringComparer.Ordinal)) WriteTag(w, tag2, token);
                        w.Write("block_states"); foreach (var tag2 in extraStates.OrderBy(t => t.Name, StringComparer.Ordinal)) WriteTag(w, tag2, token);
                        w.Write("biomes"); foreach (var tag2 in extraBiomes.OrderBy(t => t.Name, StringComparer.Ordinal)) WriteTag(w, tag2, token);
                    }
                }
            });
            var other = OtherHash(false);
            var semanticOther = OtherHash(true);
            return new(blocks, blockEntities, biomes, other)
            {
                MergeIdentity = Hash(w =>
                {
                    w.Write("minerewind/merge-content/1"); w.Write(blocks); w.Write(blockEntities); w.Write(biomes); w.Write(semanticOther);
                })
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or NbtFormatException or InvalidCastException or ArgumentException or OverflowException or EndOfStreamException)
        { return null; }
    }
    private static bool IsKnownRuntimeField(NbtTag tag) => tag.Name switch
    {
        "LastUpdate" or "InhabitedTime" => tag is NbtLong,
        "isLightOn" => tag is NbtByte,
        "block_ticks" or "fluid_ticks" => tag is NbtList list && list.ListType is NbtTagType.Compound or NbtTagType.End,
        "Heightmaps" => tag is NbtCompound maps && maps.All(t => t is NbtLongArray
            && t.Name is "WORLD_SURFACE_WG" or "WORLD_SURFACE" or "OCEAN_FLOOR_WG" or "OCEAN_FLOOR" or "MOTION_BLOCKING" or "MOTION_BLOCKING_NO_LEAVES"),
        _ => false
    };
    private static string BlockName(int version, NbtTag tag)
    {
        if (version >= 5023 && tag is NbtString shorthand) return Validate(shorthand.Value);
        if (tag is not NbtCompound compound) throw new InvalidDataException();
        var nameKey = version >= 5023 ? (compound.Contains("id") ? "id" : "") : "Name";
        var propertiesKey = version >= 5023 ? "properties" : "Properties";
        if (compound.Any(t => t.Name != nameKey && t.Name != propertiesKey)) throw new InvalidDataException();
        var name = Validate(compound.Get<NbtString>(nameKey)?.Value);
        var properties = compound.Get<NbtCompound>(propertiesKey);
        if (compound.Contains(propertiesKey) && properties is null) throw new InvalidDataException();
        if (properties is null || properties.Count == 0) return name;
        // Length-delimited canonical data retains every state property, including modded ones.
        return name + "#" + Hash(w => { foreach (var p in properties.OrderBy(t => t.Name, StringComparer.Ordinal)) { w.Write(p.Name!); w.Write(((NbtString)p).Value); } });
        static string Validate(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 ? value : throw new InvalidDataException();
    }
    private static int[] Indices(NbtCompound states, int palette, int count, int minimumBits)
    {
        if (palette < 1 || palette > count) throw new InvalidDataException();
        if (palette == 1) return new int[count];
        var bits = Math.Max(minimumBits, (int)Math.Ceiling(Math.Log2(palette))); var perLong = 64 / bits;
        var data = states.Get<NbtLongArray>("data")?.Value;
        if (data is null || data.Length != (count + perLong - 1) / perLong) throw new InvalidDataException();
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var index = (int)((unchecked((ulong)data[i / perLong]) >> (i % perLong * bits)) & ((1UL << bits) - 1));
            if (index >= palette) throw new InvalidDataException();
            result[i] = index;
        }
        return result;
    }
    private static string Hash(Action<BinaryWriter> write)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        write(writer); writer.Flush(); stream.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }
    private static void WriteTag(BinaryWriter writer, NbtTag tag, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); writer.Write((byte)tag.TagType); writer.Write(tag.Name ?? "");
        switch (tag)
        {
            case NbtCompound c: writer.Write(c.Count); foreach (var t in c.OrderBy(t => t.Name, StringComparer.Ordinal)) WriteTag(writer, t, token); break;
            case NbtList l: writer.Write((byte)l.ListType); writer.Write(l.Count); foreach (var t in l) WriteTag(writer, t, token); break;
            case NbtByte b: writer.Write(b.Value); break;
            case NbtShort s: writer.Write(s.Value); break;
            case NbtInt i: writer.Write(i.Value); break;
            case NbtLong l: writer.Write(l.Value); break;
            case NbtFloat f: writer.Write(f.Value); break;
            case NbtDouble d: writer.Write(d.Value); break;
            case NbtString s: writer.Write(s.Value); break;
            case NbtByteArray b: writer.Write(b.Value.Length); writer.Write(b.Value); break;
            case NbtIntArray a: writer.Write(a.Value.Length); foreach (var i in a.Value) writer.Write(i); break;
            case NbtLongArray a: writer.Write(a.Value.Length); foreach (var i in a.Value) writer.Write(i); break;
            default: throw new InvalidDataException();
        }
    }
}
