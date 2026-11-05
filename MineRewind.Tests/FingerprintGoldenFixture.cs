using fNbt;

namespace MineRewind.Tests;

// Independent NBT fixture, shared only by tests and the offline baseline oracle.
internal static class FingerprintGoldenFixture
{
    internal static NbtCompound Create()
    {
        var packed = new long[256];
        for (var i = 0; i < 4096; i++) packed[i / 16] |= (long)(i % 2) << (i % 16 * 4);
        NbtCompound State(string name) => new()
        {
            new NbtString("Name", name), new NbtCompound("Properties")
            { new NbtString("朝向", "北方🌲"), new NbtString("waterlogged", "false") }
        };
        return new("")
        {
            new NbtInt("DataVersion", 3465), new NbtInt("xPos", -1), new NbtInt("zPos", 0),
            new NbtLong("LastUpdate", 987654321), new NbtLong("InhabitedTime", 123), new NbtByte("isLightOn", 1),
            new NbtCompound("Heightmaps") { new NbtLongArray("WORLD_SURFACE", [1, -1, long.MinValue]) },
            new NbtList("block_ticks", NbtTagType.Compound),
            new NbtList("sections", [new NbtCompound
            {
                new NbtByte("Y", unchecked((byte)-4)), new NbtByteArray("SkyLight", new byte[2048]),
                new NbtCompound("block_states") { new NbtList("palette", [State("minecraft:oak_stairs"), State("mod:" + new string('界', 160))]), new NbtLongArray("data", packed) },
                new NbtCompound("biomes") { new NbtList("palette", [new NbtString(null, "minecraft:plains"), new NbtString(null, "mod:" + new string('界', 1500))]), new NbtLongArray("data", [unchecked((long)0xAAAAAAAAAAAAAAAA)]) }
            }, new NbtCompound
            {
                new NbtByte("Y", unchecked((byte)-3)),
                new NbtCompound("block_states") { new NbtList("palette", [new NbtCompound { new NbtString("Name", "minecraft:air") }]) }
            }]),
            new NbtList("block_entities", [new NbtCompound { new NbtString("id", "minecraft:chest"), new NbtString("text", "中文🌲"), new NbtInt("items", 7) }]),
            new NbtCompound("mod:unknown")
            {
                new NbtByte("byte", 255), new NbtShort("short", -1234), new NbtInt("int", -123456), new NbtLong("long", long.MinValue),
                new NbtFloat("float", 1.25f), new NbtDouble("double", -2.5), new NbtString("string", "x\0y🌲"),
                new NbtByteArray("bytes", [0, 1, 255]), new NbtIntArray("ints", [int.MinValue, 0, int.MaxValue]),
                new NbtLongArray("longs", [long.MinValue, 0, long.MaxValue]), new NbtList("list", [new NbtString(null, "first"), new NbtString(null, "second")])
            }
        };
    }
}
