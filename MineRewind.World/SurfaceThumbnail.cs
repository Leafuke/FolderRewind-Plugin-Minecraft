namespace MineRewind.World;

/// <summary>Compact terrain samples and area-averaged premultiplied mipmaps; never a world authority.</summary>
public sealed class SurfaceThumbnail
{
    private readonly uint[] _base = new uint[256];
    private readonly short[] _heights = new short[256];
    private readonly bool[] _shade = new bool[256];
    private readonly uint[][] _levels = [new uint[256], new uint[64], new uint[16], new uint[4], new uint[1]];
    public ChunkReadState State { get; }
    public bool HasSurface { get; }
    public bool MissingColor { get; }
    public long EstimatedBytes => 4096;
    public SurfaceThumbnail(ChunkSurface surface)
    {
        State = surface.State;
        if (surface.Columns.Count == 256)
            for (var i = 0; i < 256; i++)
            {
                if (surface.Columns[i] is not { } c) continue;
                HasSurface = true; MissingColor |= c.MissingColor;
                _heights[i] = (short)c.Height;
                _shade[i] = !c.MissingColor && c.WaterBottomHeight is null && c.BlockName is not ("minecraft:water" or "minecraft:bubble_column");
                _base[i] = c.MissingColor && ((i % 16 + i / 16) & 3) == 0 ? 0xFF686868 : SurfaceShading.Color(c, null, null, null, null);
            }
        Rebuild(null, null, null, null);
    }
    public void Rebuild(SurfaceThumbnail? west, SurfaceThumbnail? east, SurfaceThumbnail? north, SurfaceThumbnail? south)
    {
        int Height(SurfaceThumbnail? t, int i, int fallback) => t is not null && t._base[i] != 0 ? t._heights[i] : fallback;
        for (var i = 0; i < 256; i++)
        {
            var color = _base[i];
            if (_shade[i])
            {
                var x = i % 16; var z = i / 16; var h = _heights[i];
                var w = Height(x == 0 ? west : this, x == 0 ? i + 15 : i - 1, h);
                var e = Height(x == 15 ? east : this, x == 15 ? i - 15 : i + 1, h);
                var n = Height(z == 0 ? north : this, z == 0 ? i + 240 : i - 16, h);
                var s = Height(z == 15 ? south : this, z == 15 ? i - 240 : i + 16, h);
                var shade = Math.Clamp((e - w + s - n) * 4, -40, 40);
                color = 0xFF000000 | (uint)Math.Clamp((int)(color >> 16 & 255) + shade, 0, 255) << 16
                    | (uint)Math.Clamp((int)(color >> 8 & 255) + shade, 0, 255) << 8 | (uint)Math.Clamp((int)(color & 255) + shade, 0, 255);
            }
            _levels[0][i] = color;
        }
        for (var level = 1; level < 5; level++)
        {
            var size = 16 >> level; var previous = _levels[level - 1];
            for (var z = 0; z < size; z++) for (var x = 0; x < size; x++)
            {
                var i = z * 2 * size * 2 + x * 2;
                _levels[level][z * size + x] = Average(previous[i], previous[i + 1], previous[i + size * 2], previous[i + size * 2 + 1]);
            }
        }
    }
    public uint Pixel(int units, int x, int z) => _levels[(int)Math.Log2(units)][z * (16 / units) + x];
    private static uint Average(uint a, uint b, uint c, uint d)
    {
        uint Channel(int shift) => (((a >> shift) & 255) + ((b >> shift) & 255) + ((c >> shift) & 255) + ((d >> shift) & 255) + 2) / 4;
        return Channel(24) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }
}
