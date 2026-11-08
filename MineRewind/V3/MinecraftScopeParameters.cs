using System.Globalization;
using System.Text;
using static MineRewind.MinecraftRegionBackupScope;

namespace MineRewind;

internal enum MinecraftDimension { Overworld, Nether, End }
internal sealed record MinecraftScopeSelection(IReadOnlyList<MinecraftDimension> Dimensions, IReadOnlySet<(int X, int Z)> Regions);

// Host treats plugin parameters as opaque. Legacy names and units terminate at this adapter.
internal static class MinecraftScopeParameters
{
    internal static bool TryParse(IReadOnlyDictionary<string, string> parameters,
        out MinecraftScopeSelection selection, out string errorCode)
    {
        selection = new([], new HashSet<(int, int)>());
        if (!TryGetDimensions(parameters, out var dimensions, out errorCode)
            || !TryGetRegionCoordinates(parameters, out var regions, out errorCode)) return false;
        selection = new(dimensions, regions);
        return true;
    }

    private static bool TryGetRegionCoordinates(
        IReadOnlyDictionary<string, string> parameters,
        out HashSet<(int X, int Z)> coordinates,
        out string errorCode)
    {
        var areas = Get(parameters, "areas");
        if (areas.Length > 0)
            return TryParseAreas(areas, out coordinates, out errorCode);

        // 兼容 M3R 垂直切片和早期 v3 配置保存的 region 坐标，不把兼容字段重新暴露为 Host 身份。
        var regions = Get(parameters, "regions");
        if (string.IsNullOrWhiteSpace(regions)) regions = Get(parameters, "selectedRegions");
        coordinates = [];
        foreach (var token in regions.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var values = token.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 2
                || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
                || Math.Abs((long)x) > ToRegion(MaxBlockCoordinate)
                || Math.Abs((long)z) > ToRegion(MaxBlockCoordinate))
                return Fail("invalid_region_coordinate", out errorCode);
            coordinates.Add((x, z));
            if (coordinates.Count > MaxRegionsPerDimension)
                return Fail("region_limit_exceeded", out errorCode);
        }

        return coordinates.Count > 0
            ? Succeed(out errorCode)
            : Fail("region_area_required", out errorCode);
    }

    private static bool TryParseAreas(
        string text,
        out HashSet<(int X, int Z)> coordinates,
        out string errorCode)
    {
        coordinates = [];
        if (Encoding.UTF8.GetByteCount(text) > MaxAreaBytes)
            return Fail("region_input_too_large", out errorCode);
        var lines = SplitLines(text).ToArray();
        if (lines.Length == 0) return Fail("region_area_required", out errorCode);
        if (lines.Length > MaxAreaLines) return Fail("region_too_many_lines", out errorCode);

        foreach (var line in lines)
        {
            var values = line.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length != 4
                || !TryCoordinate(values[0], out var x1)
                || !TryCoordinate(values[1], out var z1)
                || !TryCoordinate(values[2], out var x2)
                || !TryCoordinate(values[3], out var z2))
                return Fail("invalid_region_area", out errorCode);

            var minX = Math.Min(ToRegion(x1), ToRegion(x2));
            var maxX = Math.Max(ToRegion(x1), ToRegion(x2));
            var minZ = Math.Min(ToRegion(z1), ToRegion(z2));
            var maxZ = Math.Max(ToRegion(z1), ToRegion(z2));
            var count = ((long)maxX - minX + 1) * ((long)maxZ - minZ + 1);
            if (count <= 0 || count > MaxRegionsPerDimension)
                return Fail("region_limit_exceeded", out errorCode);
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                coordinates.Add((x, z));
                if (coordinates.Count > MaxRegionsPerDimension)
                    return Fail("region_limit_exceeded", out errorCode);
            }
        }

        return Succeed(out errorCode);
    }

    private static bool TryGetDimensions(
        IReadOnlyDictionary<string, string> parameters,
        out IReadOnlyList<MinecraftDimension> dimensions,
        out string errorCode)
    {
        var selected = new List<MinecraftDimension>();
        var compact = Get(parameters, "dimensions");
        if (!string.IsNullOrWhiteSpace(compact))
        {
            foreach (var token in compact.Split([';', ',', '|'], StringSplitOptions.TrimEntries))
            {
                if (!TryParseDimension(token, out var dimension))
                {
                    dimensions = [];
                    return Fail("invalid_dimension", out errorCode);
                }
                if (!selected.Contains(dimension)) selected.Add(dimension);
            }
        }
        else
        {
            if (!TryBoolean(parameters, "dimension.overworld", true, out var overworld)
                || !TryBoolean(parameters, "dimension.nether", false, out var nether)
                || !TryBoolean(parameters, "dimension.end", false, out var end))
            {
                dimensions = [];
                return Fail("invalid_dimension_flag", out errorCode);
            }
            if (overworld) selected.Add(MinecraftDimension.Overworld);
            if (nether) selected.Add(MinecraftDimension.Nether);
            if (end) selected.Add(MinecraftDimension.End);
        }

        dimensions = selected;
        return selected.Count > 0
            ? Succeed(out errorCode)
            : Fail("dimension_required", out errorCode);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length > 0 && !value.StartsWith('#')) yield return value;
        }
    }

    private static bool TryCoordinate(string value, out double coordinate)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate)
           && double.IsFinite(coordinate)
           && coordinate is >= -MaxBlockCoordinate and <= MaxBlockCoordinate;

    private static int ToRegion(double coordinate) => (int)Math.Floor(coordinate / 512d);
    private static string Get(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static bool TryBoolean(IReadOnlyDictionary<string, string> values, string key, bool defaultValue, out bool result)
    {
        var value = Get(values, key);
        if (string.IsNullOrWhiteSpace(value)) { result = defaultValue; return true; }
        return bool.TryParse(value, out result);
    }

    private static bool TryParseDimension(string value, out MinecraftDimension dimension)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "overworld": case "world": case "minecraft:overworld": dimension = MinecraftDimension.Overworld; return true;
            case "nether": case "the_nether": case "minecraft:the_nether": case "dim-1": dimension = MinecraftDimension.Nether; return true;
            case "end": case "the_end": case "minecraft:the_end": case "dim1": dimension = MinecraftDimension.End; return true;
            default: dimension = default; return false;
        }
    }

    private static bool Fail(string code, out string errorCode) { errorCode = code; return false; }
    private static bool Succeed(out string errorCode) { errorCode = string.Empty; return true; }
}
