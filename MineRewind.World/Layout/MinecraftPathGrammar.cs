using System.Globalization;
using MineRewind.World.Semantic;

namespace MineRewind.World;

/// <summary>Path syntax only. Recognition never grants capture or mutation authority.</summary>
public static class MinecraftPathGrammar
{
    public const string Overworld = "minecraft:overworld";
    public const string Nether = "minecraft:the_nether";
    public const string End = "minecraft:the_end";
    public static IReadOnlyList<string> Components { get; } = Array.AsReadOnly(new[] { "region", "entities", "poi" });

    public static bool TryRegionName(string path, out RegionKey key)
    {
        key = default;
        var parts = Path.GetFileName(path).Split('.');
        if (parts.Length != 4 || parts[0] != "r" || parts[3] != "mca"
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
            || Math.Abs((long)x) > 58_594 || Math.Abs((long)z) > 58_594) return false;
        key = new(x, z); return true;
    }

    public static bool TryExternalName(string path, out ChunkKey key)
    {
        key = default;
        var parts = Path.GetFileName(path).Split('.');
        if (parts.Length != 4 || parts[0] != "c" || parts[3] != "mcc"
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z)) return false;
        key = new(x, z); return true;
    }

    public static string[] WorldRoots(IEnumerable<string> paths) => paths
        .Where(p => p == "level.dat" || p.EndsWith("/level.dat", StringComparison.Ordinal))
        .Select(p => p[..^9].TrimEnd('/')).Distinct(StringComparer.Ordinal)
        .OrderByDescending(p => p.Length).ToArray();

    public static bool TryDimension(string relative, out string id, out WorldLayoutFamily family)
    {
        family = WorldLayoutFamily.Legacy;
        id = relative switch { "" => Overworld, "DIM-1" => Nether, "DIM1" => End, _ => "" };
        if (id.Length != 0) return true;
        var parts = relative.Split('/');
        if (parts.Length < 3 || parts[0] != "dimensions" || parts.Any(p => p.Length == 0 || p is "." or "..")) return false;
        id = parts[1] + ":" + string.Join('/', parts[2..]); family = WorldLayoutFamily.Namespaced; return true;
    }

    public static bool TryRegionAddress(string path, IReadOnlyList<string> worlds, out RegionAddress? address)
    {
        address = null;
        if (!path.EndsWith(".mca", StringComparison.Ordinal) || !TryRegionName(path, out var region)) return false;
        var world = worlds.FirstOrDefault(w => w.Length == 0 || path.StartsWith(w + "/", StringComparison.Ordinal)) ?? "";
        var relative = world.Length == 0 ? path : path[(world.Length + 1)..];
        var parts = relative.Split('/');
        if (parts.Length < 2) return false;
        var component = parts[^2] switch { "region" => ChunkComponentKind.Terrain, "entities" => ChunkComponentKind.Entities,
            "poi" => ChunkComponentKind.Poi, _ => (ChunkComponentKind)(-1) };
        if ((int)component < 0 || !TryDimension(string.Join('/', parts[..^2]), out var dimension, out _)) return false;
        address = new(world.Length == 0 ? "." : world, dimension, region.X, region.Z, component); return true;
    }

    public static bool TryLevelName(string line, out string? name)
    {
        name = null; line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) return false;
        var separator = line.IndexOf('=');
        if (separator < 0 || !line[..separator].Trim().Equals("level-name", StringComparison.OrdinalIgnoreCase)) return false;
        var value = line[(separator + 1)..].Trim(); name = value.Length == 0 ? null : value; return true;
    }
}
