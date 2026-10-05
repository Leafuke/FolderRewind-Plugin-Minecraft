namespace MineRewind.World;

/// <summary>Height/water shading adapted from MCA Selector TileImage (MIT; see Colors/MCASelector.LICENSE.txt).</summary>
public static class SurfaceShading
{
    public static uint Color(SurfaceColumn center, SurfaceColumn? west, SurfaceColumn? east, SurfaceColumn? north, SurfaceColumn? south)
    {
        if (center.WaterBottomHeight is { } bottom && center.WaterBottomColor is { } bottomColor)
        {
            var weight = Math.Clamp(.5 - (center.Height - bottom) / 80d, 0, .5);
            return Channels(bottomColor, center.Argb, weight);
        }
        if (center.MissingColor || center.BlockName is "minecraft:water" or "minecraft:bubble_column") return center.Argb;
        var slope = ((east?.Height ?? center.Height) - (west?.Height ?? center.Height))
            + ((south?.Height ?? center.Height) - (north?.Height ?? center.Height));
        var shade = Math.Clamp(slope * 4, -40, 40);
        return 0xFF000000 | (uint)Math.Clamp((int)(center.Argb >> 16 & 255) + shade, 0, 255) << 16
            | (uint)Math.Clamp((int)(center.Argb >> 8 & 255) + shade, 0, 255) << 8
            | (uint)Math.Clamp((int)(center.Argb & 255) + shade, 0, 255);
    }
    private static uint Channels(uint bottom, uint top, double weight)
    {
        uint channel(int shift) => (uint)Math.Clamp((int)((bottom >> shift & 255) * weight + (top >> shift & 255) * (1 - weight)), 0, 255);
        return 0xFF000000 | channel(16) << 16 | channel(8) << 8 | channel(0);
    }
}
