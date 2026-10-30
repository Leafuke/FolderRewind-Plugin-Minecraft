using System.Globalization;

namespace MineRewind.World;

public readonly record struct RegionKey(int X, int Z);
public readonly record struct ChunkKey(int X, int Z)
{
    public RegionKey Region => new(Coordinates.FloorDiv(X, 32), Coordinates.FloorDiv(Z, 32));
    public int Slot => Coordinates.FloorMod(Z, 32) * 32 + Coordinates.FloorMod(X, 32);
}

public static class Coordinates
{
    public static int FloorDiv(int value, int divisor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(divisor);
        return (int)Math.Floor((double)value / divisor);
    }
    public static int FloorMod(int value, int divisor) => (int)((long)value - (long)FloorDiv(value, divisor) * divisor);
    public static ChunkKey ChunkAt(int blockX, int blockZ) => new(FloorDiv(blockX, 16), FloorDiv(blockZ, 16));
}

public enum ChunkComponentKind { Terrain, Entities, Poi }
public enum SurfaceViewMode { Surface, Cave, Slice }
public enum ChunkReadState { Present, Absent, Corrupt, UnsupportedVersion, UnsupportedCompression, Changing, Unavailable }
public sealed record WorldDescriptor(string SourceRoot, string WorldRoot, IReadOnlyList<DimensionDescriptor> Dimensions);
public sealed record DimensionDescriptor(string Id, string Root, IReadOnlyDictionary<RegionKey, string> Regions);
public sealed record SurfaceColumn(string BlockName, int Height, uint Argb,
    string Biome = "minecraft:plains", bool MissingColor = false, int? WaterBottomHeight = null, uint? WaterBottomColor = null);
public sealed record ChunkSurface(ChunkKey Key, ChunkReadState State, int? DataVersion, IReadOnlyList<SurfaceColumn?> Columns)
{
    public static ChunkSurface Empty(ChunkKey key, ChunkReadState state, int? version = null) => new(key, state, version, Array.Empty<SurfaceColumn?>());
}

/// <summary>All paths are constrained to the configured source, including every intermediate directory.</summary>
public static class WorldPaths
{
    public static string RequireContained(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("World path is outside the configured source.");
        var current = root;
        Check(current);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            Check(current);
        }
        return path;
        static void Check(string candidate)
        {
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("World preview cannot follow filesystem links.");
        }
    }

    public static bool TryRegionName(string path, out RegionKey key)
    {
        key = default;
        var parts = Path.GetFileName(path).Split('.');
        if (parts.Length != 4 || parts[0] != "r" || parts[3] != "mca"
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
            || Math.Abs((long)x) > 58_594 || Math.Abs((long)z) > 58_594) return false;
        key = new(x, z);
        return true;
    }
}
