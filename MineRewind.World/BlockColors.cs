using System.Globalization;
using System.Text.Json;
using fNbt;

namespace MineRewind.World;

public readonly record struct SurfaceMaterial(string Name, uint Color, int Flags, bool Missing, bool Waterlogged);

/// <summary>Lookup/tint adapted from MCA Selector (MIT); provenance and license are in Colors/.</summary>
public static class BlockColors
{
    public const uint Missing = 0xFF969696;
    private static readonly (int Version, Lazy<Mapping> Map)[] Maps =
    [
        (2834, new(() => new("21w37a"))), (3080, new(() => new("22w11a"))),
        (3207, new(() => new("23w12a"))), (3940, new(() => new("24w18a"))),
        (4536, new(() => new("25w32a"))), (4764, new(() => new("26.1-snapshot-1"))),
        (4883, new(() => new("26.2-snapshot-1"))), (4998, new(() => new("26.3-snapshot-1")))
    ];
    private static Mapping ForVersion(int version) => Maps.Last(m => m.Version <= version).Map.Value;
    public static uint For(string name) => Tint(4786, Material(4786, name, null), "minecraft:plains");
    public static SurfaceMaterial Material(int version, string name, NbtCompound? properties)
    {
        var map = ForVersion(version);
        var waterlogged = properties?.Get<NbtString>("waterlogged")?.Value == "true";
        if (!map.Colors.TryGetValue(name, out var entry)) return new(name, Missing, 0, true, waterlogged);
        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("states", out var states))
        {
            var bytes = new byte[map.StateBytes];
            if (properties is not null)
                foreach (var property in properties.OfType<NbtString>())
                    if (map.States.TryGetValue((property.Name!, property.Value), out var bit)) bytes[bit / 8] |= (byte)(1 << (bit % 8));
            var length = bytes.Length;
            while (length > 0 && bytes[length - 1] == 0) length--;
            entry = states.TryGetProperty(Convert.ToBase64String(bytes, 0, length), out var color) ? color : entry.GetProperty("default");
        }
        var flags = entry.ValueKind == JsonValueKind.Object ? entry.GetProperty("properties").GetInt32() : 0;
        var hex = entry.ValueKind == JsonValueKind.String ? entry.GetString()! : entry.GetProperty("color").GetString()!;
        return new(name, 0xFF000000 | uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture), flags, false, waterlogged);
    }
    public static uint Tint(int version, SurfaceMaterial material, string biome)
    {
        var index = (material.Flags & 2) != 0 ? 0 : (material.Flags & 4) != 0 ? 1 : (material.Flags & 8) != 0 ? 2 : (material.Flags & 128) != 0 ? 3 : -1;
        if (index < 0) return material.Color;
        var map = ForVersion(version);
        if (!map.Tints.TryGetValue(biome, out var colors)) colors = map.Tints["minecraft:plains"];
        if (index >= colors.Length) return material.Color;
        var tint = colors[index]; var color = material.Color;
        return 0xFF000000 | (((color >> 16 & 255) * (tint >> 16 & 255) >> 8) << 16)
            | (((color >> 8 & 255) * (tint >> 8 & 255) >> 8) << 8) | ((color & 255) * (tint & 255) >> 8);
    }
    private sealed class Mapping
    {
        public Dictionary<string, JsonElement> Colors { get; }
        public Dictionary<string, uint[]> Tints { get; }
        public Dictionary<(string, string), int> States { get; } = [];
        public int StateBytes { get; }
        public Mapping(string name)
        {
            using var stream = typeof(BlockColors).Assembly.GetManifestResourceStream($"MineRewind.World.Colors.colors_{name}.json")
                ?? throw new InvalidOperationException("Missing bundled map colors.");
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            Colors = root.GetProperty("colors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            Tints = root.GetProperty("tints").EnumerateObject().ToDictionary(p => p.Name,
                p => p.Value.EnumerateArray().Select(c => uint.Parse(c.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray(), StringComparer.Ordinal);
            foreach (var property in root.GetProperty("states").EnumerateObject())
                foreach (var value in property.Value.EnumerateObject()) States.Add((property.Name, value.Name), value.Value.GetInt32());
            StateBytes = (States.Values.Max() + 8) / 8;
        }
    }
}
