using fNbt;

namespace MineRewind.World;

/// <summary>Normalizes supported on-disk palette entries without rewriting their source bytes.</summary>
internal static class PreviewBlockState
{
    public static SurfaceMaterial Read(int version, NbtTag tag)
    {
        if (version >= 5023)
        {
            if (tag is NbtString shorthand) return Material(version, shorthand.Value, null);
            if (tag is NbtCompound compound)
                return Material(version, compound.Get<NbtString>("id")?.Value ?? compound.Get<NbtString>("")?.Value,
                    compound.Get<NbtCompound>("properties"));
        }
        else if (tag is NbtCompound legacy)
            return Material(version, legacy.Get<NbtString>("Name")?.Value, legacy.Get<NbtCompound>("Properties"));
        throw new InvalidDataException("Invalid block state palette entry.");
    }

    private static SurfaceMaterial Material(int version, string? name, NbtCompound? properties)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512) throw new InvalidDataException("Missing or invalid block id.");
        return BlockColors.Material(version, name, properties);
    }
}
