namespace MineRewind.World;

/// <summary>Original approximate colors; no Minecraft textures or extracted game resources.</summary>
public static class BlockColors
{
    public static uint For(string name)
    {
        if (!name.StartsWith("minecraft:", StringComparison.Ordinal)) return 0xFFD66CDD;
        var block = name[10..];
        return block switch
        {
            "water" or "bubble_column" => 0xFF3976C4,
            "lava" or "magma_block" => 0xFFF47A23,
            "grass_block" or "moss_block" or "moss_carpet" => 0xFF70994E,
            "dirt" or "coarse_dirt" or "rooted_dirt" or "farmland" or "dirt_path" => 0xFF927052,
            "podzol" or "mud" or "packed_mud" => 0xFF695749,
            "mycelium" => 0xFF847387,
            "sand" or "sandstone" or "smooth_sandstone" or "end_stone" or "end_stone_bricks" => 0xFFD7CE94,
            "red_sand" or "red_sandstone" or "terracotta" => 0xFFB97649,
            "snow" or "snow_block" or "powder_snow" or "white_wool" or "white_concrete" => 0xFFF0F0EA,
            "ice" or "packed_ice" or "blue_ice" or "frosted_ice" => 0xFF8EBBDE,
            "netherrack" or "nether_bricks" => 0xFF864640,
            "soul_sand" or "soul_soil" => 0xFF655042,
            "crimson_nylium" or "nether_wart_block" => 0xFF9D394B,
            "warped_nylium" or "warped_wart_block" => 0xFF397C77,
            "obsidian" or "crying_obsidian" => 0xFF352A4A,
            "bedrock" or "deepslate" or "cobbled_deepslate" or "blackstone" or "basalt" => 0xFF50525A,
            "stone" or "cobblestone" or "andesite" or "gravel" or "stone_bricks" => 0xFF8B8C88,
            "granite" => 0xFFA77866,
            "diorite" or "calcite" or "quartz_block" => 0xFFD1CEBD,
            "tuff" => 0xFF74776B,
            _ when block.Contains("cherry", StringComparison.Ordinal) => 0xFFE1A3B7,
            _ when block.Contains("leaves", StringComparison.Ordinal) || block.Contains("grass", StringComparison.Ordinal) || block.Contains("fern", StringComparison.Ordinal) => 0xFF4F813B,
            _ when block.Contains("log", StringComparison.Ordinal) || block.Contains("wood", StringComparison.Ordinal) || block.Contains("planks", StringComparison.Ordinal) => 0xFF9D8153,
            _ when block.Contains("copper", StringComparison.Ordinal) => 0xFFB66E4D,
            _ when block.Contains("glass", StringComparison.Ordinal) => 0xFFB2C8CB,
            _ when block.Contains("brick", StringComparison.Ordinal) => 0xFF986B61,
            _ => 0xFFD66CDD
        };
    }
}
