using fNbt;
using FolderRewind.Services;
using System.Globalization;

namespace MineRewind
{
    /// <summary>
    /// Minecraft NBT 工具类 —— 基于 fNbt 库。
    /// 用于在还原存档时读取/写回 level.dat 中的玩家数据，
    /// 从而实现"还原存档但保留玩家位置和物品栏"等功能。
    /// 自动适配 26.1 前后两种存档格式。
    ///
    /// Minecraft level.dat 结构（旧版，&lt; 26.1）：
    ///   根 TAG_Compound ""
    ///     └─ TAG_Compound "Data"
    ///          ├─ TAG_Compound "Player"
    ///          │    ├─ TAG_List "Pos"          (3 × TAG_Double: X, Y, Z)
    ///          │    ├─ TAG_List "Rotation"     (2 × TAG_Float: Yaw, Pitch)
    ///          │    ├─ TAG_List "Inventory"    (TAG_Compound[])
    ///          │    ├─ TAG_List "EnderItems"   (TAG_Compound[])
    ///          │    ├─ TAG_Int "XpLevel"
    ///          │    ├─ TAG_Float "XpP"
    ///          │    ├─ TAG_Int "XpTotal"
    ///          │    ├─ TAG_Int "Score"
    ///          │    ├─ TAG_Int "playerGameType"
    ///          │    ├─ TAG_String "Dimension"  (1.16+: 命名空间字符串)
    ///          │    ├─ TAG_Short "Health"
    ///          │    ├─ TAG_Short "HurtTime"
    ///          │    ├─ TAG_Int "foodLevel"
    ///          │    ├─ TAG_Float "foodSaturationLevel"
    ///          │    └─ ...
    ///          └─ ...
    ///
    /// Minecraft 26.1+ 单人存档结构：
    ///   level.dat/Data            → 只保留 LevelName, GameType, Time, DayTime,
    ///                               LastPlayed, DataVersion, singleplayer_uuid
    ///   players/data/&lt;uuid&gt;.dat   → 玩家数据（根标签即玩家复合标签）
    ///   data/minecraft/world_gen_settings.dat → 种子和世界生成设置
    /// </summary>
    public static class NbtHelper
    {
        /// <summary>
        /// 玩家数据快照 —— 在还原前从当前 level.dat 提取，还原后写回。
        /// </summary>
        public sealed class PlayerDataSnapshot
        {
            /// <summary>玩家位置 (Pos: 3×Double)</summary>
            public NbtList? Pos { get; set; }

            /// <summary>玩家朝向 (Rotation: 2×Float)</summary>
            public NbtList? Rotation { get; set; }

            /// <summary>玩家所在维度</summary>
            public NbtTag? Dimension { get; set; }

            /// <summary>玩家物品栏</summary>
            public NbtList? Inventory { get; set; }

            /// <summary>末影箱</summary>
            public NbtList? EnderItems { get; set; }

            /// <summary>经验等级</summary>
            public NbtInt? XpLevel { get; set; }

            /// <summary>经验进度 (0.0 ~ 1.0)</summary>
            public NbtFloat? XpP { get; set; }

            /// <summary>总经验值</summary>
            public NbtInt? XpTotal { get; set; }

            /// <summary>分数</summary>
            public NbtInt? Score { get; set; }

            /// <summary>游戏模式</summary>
            public NbtInt? PlayerGameType { get; set; }

            /// <summary>生命值</summary>
            public NbtShort? Health { get; set; }

            /// <summary>饱食度</summary>
            public NbtInt? FoodLevel { get; set; }

            /// <summary>饱和度</summary>
            public NbtFloat? FoodSaturationLevel { get; set; }

            /// <summary>是否包含有效数据</summary>
            public bool HasData =>
                Pos != null || Inventory != null || EnderItems != null ||
                XpLevel != null || Health != null || FoodLevel != null;
        }

        /// <summary>
        /// Minecraft 世界基础详情，用于”文件夹详情”对话框展示。
        /// 这里只返回数据，不做 UI 本地化。
        /// </summary>
        public sealed class MinecraftWorldDetails
        {
            public string LevelName { get; init; } = string.Empty;

            public string GameMode { get; init; } = string.Empty;

            public string Seed { get; init; } = string.Empty;

            public long? TotalTime { get; init; }

            public long? DayTime { get; init; }

            public long? LastPlayed { get; init; }

            public bool HasPlayerData { get; init; }

            /// <summary>世界的 NBT DataVersion（所有现代版本均存在），用于调试和版本判断。</summary>
            public int? DataVersion { get; init; }

            /// <summary>是否为 Minecraft 26.1+ 新版存档格式。</summary>
            public bool IsNewFormat { get; init; }
        }

        /// <summary>
        /// 从 level.dat 中读取玩家数据快照。
        /// 自动适配 26.1 前后两种存档格式。
        /// </summary>
        /// <param name="worldPath">存档根目录路径（包含 level.dat 的目录）</param>
        /// <param name="preservePosition">是否提取位置数据</param>
        /// <param name="preserveInventory">是否提取物品栏数据</param>
        /// <param name="preserveStats">是否提取经验/生命值等状态数据</param>
        /// <returns>玩家数据快照，若 level.dat 不存在或无 Player 节点则返回 null</returns>
        public static PlayerDataSnapshot? ExtractPlayerData(
            string worldPath,
            bool preservePosition = true,
            bool preserveInventory = true,
            bool preserveStats = true)
        {
            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
            {
                LogService.LogWarning("[NbtHelper] level.dat not found, skipping player data extraction.", "MineRewind");
                return null;
            }

            try
            {
                var nbtFile = new NbtFile();
                nbtFile.LoadFromFile(levelDatPath);

                var data = nbtFile.RootTag["Data"] as NbtCompound;
                if (data == null)
                {
                    return null;
                }

                if (IsPost26_1Format(worldPath))
                {
                    return ExtractPlayerDataFromNewFormat(worldPath, data, preservePosition, preserveInventory, preserveStats);
                }

                return ExtractPlayerDataFromLegacyFormat(data, preservePosition, preserveInventory, preserveStats);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[NbtHelper] Failed to extract player data: {ex.Message}", "MineRewind", ex);
                return null;
            }
        }

        /// <summary>
        /// 旧版格式：从 level.dat/Data/Player 提取玩家数据。
        /// </summary>
        private static PlayerDataSnapshot? ExtractPlayerDataFromLegacyFormat(
            NbtCompound data,
            bool preservePosition,
            bool preserveInventory,
            bool preserveStats)
        {
            var player = data["Player"] as NbtCompound;

            if (player == null)
            {
                LogService.LogInfo("[NbtHelper] No Player compound in level.dat (server save or new world?).", "MineRewind");
                return null;
            }

            return BuildPlayerSnapshot(player, preservePosition, preserveInventory, preserveStats);
        }

        /// <summary>
        /// 26.1+ 格式：从 players/data/<uuid>.dat 提取玩家数据。
        /// 单人存档的 level.dat 不再内嵌 Player，改为 singleplayer_uuid（NbtIntArray，4 ints）引用。
        /// </summary>
        private static PlayerDataSnapshot? ExtractPlayerDataFromNewFormat(
            string worldPath,
            NbtCompound data,
            bool preservePosition,
            bool preserveInventory,
            bool preserveStats)
        {
            var uuid = GetSinglePlayerUuid(data);
            if (string.IsNullOrWhiteSpace(uuid))
            {
                LogService.LogInfo("[NbtHelper] No singleplayer_uuid in 26.1+ level.dat (server save?).", "MineRewind");
                return null;
            }

            var playerDatPath = Path.Combine(worldPath, "players", "data", $"{uuid}.dat");
            if (!File.Exists(playerDatPath))
            {
                LogService.LogInfo($"[NbtHelper] Player data file not found: {playerDatPath}", "MineRewind");
                return null;
            }

            try
            {
                var playerFile = new NbtFile();
                playerFile.LoadFromFile(playerDatPath);

                // 玩家 .dat 文件的根复合标签就是玩家数据本身（无 Data/Player 包裹层）
                var playerRoot = playerFile.RootTag;
                return BuildPlayerSnapshot(playerRoot, preservePosition, preserveInventory, preserveStats);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[NbtHelper] Failed to read 26.1+ player data: {ex.Message}", "MineRewind");
                return null;
            }
        }

        /// <summary>
        /// 从玩家 NBT 复合标签中构建 PlayerDataSnapshot。
        /// 同时适用于旧版 level.dat/Data/Player 和 26.1+ 的独立玩家 .dat 文件根标签。
        /// </summary>
        private static PlayerDataSnapshot? BuildPlayerSnapshot(
            NbtCompound player,
            bool preservePosition,
            bool preserveInventory,
            bool preserveStats)
        {
            var snapshot = new PlayerDataSnapshot();

            if (preservePosition)
            {
                snapshot.Pos = CloneTag<NbtList>(player, "Pos");
                snapshot.Rotation = CloneTag<NbtList>(player, "Rotation");
                snapshot.Dimension = CloneTag(player, "Dimension");
            }

            if (preserveInventory)
            {
                snapshot.Inventory = CloneTag<NbtList>(player, "Inventory");
                snapshot.EnderItems = CloneTag<NbtList>(player, "EnderItems");
            }

            if (preserveStats)
            {
                snapshot.XpLevel = CloneTag<NbtInt>(player, "XpLevel");
                snapshot.XpP = CloneTag<NbtFloat>(player, "XpP");
                snapshot.XpTotal = CloneTag<NbtInt>(player, "XpTotal");
                snapshot.Score = CloneTag<NbtInt>(player, "Score");
                snapshot.PlayerGameType = CloneTag<NbtInt>(player, "playerGameType");
                snapshot.Health = CloneTag<NbtShort>(player, "Health");
                snapshot.FoodLevel = CloneTag<NbtInt>(player, "foodLevel");
                snapshot.FoodSaturationLevel = CloneTag<NbtFloat>(player, "foodSaturationLevel");
            }

            if (!snapshot.HasData)
            {
                LogService.LogInfo("[NbtHelper] Player compound exists but no relevant data extracted.", "MineRewind");
                return null;
            }

            LogService.LogInfo($"[NbtHelper] Player data extracted (pos={snapshot.Pos != null}, inv={snapshot.Inventory != null}, stats={snapshot.XpLevel != null}).", "MineRewind");
            return snapshot;
        }

        /// <summary>
        /// 将先前提取的玩家数据写回 level.dat。
        /// 自动适配 26.1 前后两种存档格式。
        /// 应在还原操作完成后调用。
        /// </summary>
        /// <param name="worldPath">存档根目录路径</param>
        /// <param name="snapshot">之前通过 ExtractPlayerData 获取的快照</param>
        /// <returns>是否成功写回</returns>
        public static bool ApplyPlayerData(string worldPath, PlayerDataSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.HasData)
            {
                LogService.LogInfo("[NbtHelper] No player data to apply, skipping.", "MineRewind");
                return false;
            }

            try
            {
                if (IsPost26_1Format(worldPath))
                {
                    return ApplyPlayerDataToNewFormat(worldPath, snapshot);
                }

                return ApplyPlayerDataToLegacyFormat(worldPath, snapshot);
            }
            catch (Exception ex)
            {
                LogService.LogError($"[NbtHelper] Failed to apply player data: {ex.Message}", "MineRewind", ex);
                return false;
            }
        }

        /// <summary>
        /// 旧版格式：将玩家数据写回 level.dat/Data/Player。
        /// </summary>
        private static bool ApplyPlayerDataToLegacyFormat(string worldPath, PlayerDataSnapshot snapshot)
        {
            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
            {
                LogService.LogWarning("[NbtHelper] Restored level.dat not found, cannot apply player data.", "MineRewind");
                return false;
            }

            var nbtFile = new NbtFile();
            nbtFile.LoadFromFile(levelDatPath);

            var data = nbtFile.RootTag["Data"] as NbtCompound;
            if (data == null)
            {
                LogService.LogWarning("[NbtHelper] No 'Data' compound in restored level.dat.", "MineRewind");
                return false;
            }

            var player = data["Player"] as NbtCompound;
            if (player == null)
            {
                LogService.LogWarning("[NbtHelper] No 'Player' compound in restored level.dat, creating one.", "MineRewind");
                player = new NbtCompound("Player");
                data.Add(player);
            }

            ApplySnapshotToPlayerCompound(player, snapshot);

            nbtFile.SaveToFile(levelDatPath, NbtCompression.GZip);

            LogService.LogInfo("[NbtHelper] Player data successfully applied to restored level.dat.", "MineRewind");
            return true;
        }

        /// <summary>
        /// 26.1+ 格式：将玩家数据写回 players/data/<uuid>.dat。
        /// singleplayer_uuid 为 NbtIntArray（4 ints），需转换为 UUID 字符串。
        /// </summary>
        private static bool ApplyPlayerDataToNewFormat(string worldPath, PlayerDataSnapshot snapshot)
        {
            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
            {
                LogService.LogWarning("[NbtHelper] Restored level.dat not found, cannot determine player UUID.", "MineRewind");
                return false;
            }

            string? uuid;
            try
            {
                var levelNbt = new NbtFile();
                levelNbt.LoadFromFile(levelDatPath);
                var data = levelNbt.RootTag["Data"] as NbtCompound;
                uuid = data != null ? GetSinglePlayerUuid(data) : null;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[NbtHelper] Failed to read singleplayer_uuid from level.dat: {ex.Message}", "MineRewind");
                return false;
            }

            if (string.IsNullOrWhiteSpace(uuid))
            {
                LogService.LogWarning("[NbtHelper] No singleplayer_uuid in 26.1+ level.dat, cannot apply player data.", "MineRewind");
                return false;
            }

            var playerDatPath = Path.Combine(worldPath, "players", "data", $"{uuid}.dat");
            if (!File.Exists(playerDatPath))
            {
                LogService.LogWarning($"[NbtHelper] Player data file not found after restore: {playerDatPath}", "MineRewind");
                return false;
            }

            var playerFile = new NbtFile();
            playerFile.LoadFromFile(playerDatPath);

            // 玩家 .dat 文件的根复合标签就是玩家数据本身
            ApplySnapshotToPlayerCompound(playerFile.RootTag, snapshot);

            playerFile.SaveToFile(playerDatPath, NbtCompression.GZip);

            LogService.LogInfo("[NbtHelper] Player data successfully applied to 26.1+ player file.", "MineRewind");
            return true;
        }

        /// <summary>
        /// 将 PlayerDataSnapshot 中的标签写入目标玩家复合标签。
        /// 同时适用于旧版 level.dat/Data/Player 和 26.1+ 的独立玩家 .dat 文件根标签。
        /// </summary>
        private static void ApplySnapshotToPlayerCompound(NbtCompound player, PlayerDataSnapshot snapshot)
        {
            ReplaceTag(player, snapshot.Pos);
            ReplaceTag(player, snapshot.Rotation);
            ReplaceTag(player, snapshot.Dimension);
            ReplaceTag(player, snapshot.Inventory);
            ReplaceTag(player, snapshot.EnderItems);
            ReplaceTag(player, snapshot.XpLevel);
            ReplaceTag(player, snapshot.XpP);
            ReplaceTag(player, snapshot.XpTotal);
            ReplaceTag(player, snapshot.Score);
            ReplaceTag(player, snapshot.PlayerGameType);
            ReplaceTag(player, snapshot.Health);
            ReplaceTag(player, snapshot.FoodLevel);
            ReplaceTag(player, snapshot.FoodSaturationLevel);
        }

        /// <summary>
        /// 读取 level.dat 并返回 Minecraft 世界详情（用于详情对话框展示）。
        /// 自动适配 26.1 前后两种存档格式。
        /// 失败时返回 null，由调用方决定如何降级。
        /// </summary>
        public static MinecraftWorldDetails? TryGetWorldDetails(string worldPath)
        {
            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
            {
                return null;
            }

            try
            {
                var nbtFile = new NbtFile();
                nbtFile.LoadFromFile(levelDatPath);

                var data = nbtFile.RootTag["Data"] as NbtCompound;
                if (data == null)
                {
                    return null;
                }

                // DataVersion 在所有现代版本（1.9+）中都存在
                int? dataVersion = (data["DataVersion"] as NbtInt)?.Value;

                bool isNewFormat = IsPost26_1Format(worldPath);

                long? directSeed = (data["RandomSeed"] as NbtLong)?.Value;
                long? nestedSeed = ((data["WorldGenSettings"] as NbtCompound)?["seed"] as NbtLong)?.Value;
                int? gameType = (data["GameType"] as NbtInt)?.Value;

                // 26.1+ 的种子在独立文件中，位于 data/minecraft/world_gen_settings.dat
                string seedString;
                if (isNewFormat)
                {
                    seedString = TryReadSeedFromNewFormat(worldPath);
                }
                else
                {
                    seedString = (directSeed ?? nestedSeed)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                }

                // 26.1+ 的玩家数据在 players/data/<uuid>.dat，level.dat 只保留 singleplayer_uuid
                bool hasPlayerData;
                if (isNewFormat)
                {
                    hasPlayerData = HasNewFormatPlayerData(worldPath, data);
                }
                else
                {
                    hasPlayerData = data["Player"] is NbtCompound;
                }

                return new MinecraftWorldDetails
                {
                    LevelName = (data["LevelName"] as NbtString)?.Value ?? string.Empty,
                    GameMode = ResolveGameMode(gameType),
                    Seed = seedString,
                    TotalTime = (data["Time"] as NbtLong)?.Value,
                    DayTime = (data["DayTime"] as NbtLong)?.Value,
                    LastPlayed = (data["LastPlayed"] as NbtLong)?.Value,
                    HasPlayerData = hasPlayerData,
                    DataVersion = dataVersion,
                    IsNewFormat = isNewFormat
                };
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[NbtHelper] Failed to read world details: {ex.Message}", "MineRewind");
                return null;
            }
        }

        /// <summary>
        /// 从 26.1+ 存档的 data/minecraft/world_gen_settings.dat 中读取种子。
        /// 文件结构为 { data: { seed: &lt;long&gt;, dimensions: { ... }, ... } }，有 data 包裹层。
        /// </summary>
        private static string TryReadSeedFromNewFormat(string worldPath)
        {
            var seedFilePath = Path.Combine(worldPath, "data", "minecraft", "world_gen_settings.dat");
            if (!File.Exists(seedFilePath))
            {
                LogService.LogInfo("[NbtHelper] world_gen_settings.dat not found in 26.1+ world, seed unavailable.", "MineRewind");
                return string.Empty;
            }

            try
            {
                var nbtFile = new NbtFile();
                nbtFile.LoadFromFile(seedFilePath);

                // world_gen_settings.dat 有 data 包裹层，seed 在 data 复合标签下
                var data = nbtFile.RootTag["data"] as NbtCompound;
                var seed = data?["seed"] as NbtLong;
                return seed?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[NbtHelper] Failed to read seed from world_gen_settings.dat: {ex.Message}", "MineRewind");
                return string.Empty;
            }
        }

        /// <summary>
        /// 将 NBT 的 4-int UUID（NbtIntArray）转换为标准 UUID 字符串。
        /// Minecraft 将 128 位 UUID 存储为 [I; a, b, c, d] 四个大端序 32 位整数。
        /// </summary>
        private static string? ConvertNbtUuidToString(NbtIntArray? uuidTag)
        {
            if (uuidTag == null || uuidTag.Value.Length != 4)
                return null;

            var ints = uuidTag.Value;
            var bytes = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                bytes[i * 4] = (byte)(ints[i] >> 24);
                bytes[i * 4 + 1] = (byte)(ints[i] >> 16);
                bytes[i * 4 + 2] = (byte)(ints[i] >> 8);
                bytes[i * 4 + 3] = (byte)(ints[i]);
            }

            // Guid(byte[]) 使用的是混合字节序：前 4/2/2 字节段按小端解释，后 8 字节保持原样。
            // Minecraft NBT 中的 UUID 是标准大端序 128 位值，因此这里要先调整顺序。
            var guidBytes = new byte[16]
            {
                bytes[3], bytes[2], bytes[1], bytes[0],
                bytes[5], bytes[4],
                bytes[7], bytes[6],
                bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]
            };

            return new Guid(guidBytes).ToString();
        }

        /// <summary>
        /// 从 level.dat/Data 中读取 singleplayer_uuid（NbtIntArray，4 个整数），
        /// 转换为 UUID 字符串。返回 null 表示无单人玩家数据。
        /// </summary>
        private static string? GetSinglePlayerUuid(NbtCompound data)
        {
            return ConvertNbtUuidToString(data["singleplayer_uuid"] as NbtIntArray);
        }

        /// <summary>
        /// 检测 26.1+ 存档是否有单人玩家数据。
        /// 通过 singleplayer_uuid（NbtIntArray，4 ints）查找 players/data/<uuid>.dat。
        /// </summary>
        private static bool HasNewFormatPlayerData(string worldPath, NbtCompound data)
        {
            var uuid = GetSinglePlayerUuid(data);
            if (string.IsNullOrWhiteSpace(uuid))
            {
                return false;
            }
            LogService.LogInfo($"[NbtHelper] singleplayer_uuid found: {uuid}", "MineRewind");
            var playerDatPath = Path.Combine(worldPath, "players", "data", $"{uuid}.dat");
            return File.Exists(playerDatPath);
        }

        private static string ResolveGameMode(int? gameType)
        {
            return gameType switch
            {
                0 => "Survival",
                1 => "Creative",
                2 => "Adventure",
                3 => "Spectator",
                _ => string.Empty
            };
        }

        #region 私有辅助方法

        /// <summary>
        /// 通过文件系统检测存档是否为 Minecraft 26.1+ 新格式。
        /// 可供 RegionBackup.cs 等其他代码路径复用。
        /// </summary>
        internal static bool IsPost26_1Format(string worldPath)
        {
            return Directory.Exists(Path.Combine(worldPath, "dimensions", "minecraft", "overworld"))
                || Directory.Exists(Path.Combine(worldPath, "data", "minecraft"));
        }

        /// <summary>
        /// 克隆一个 NBT 标签。fNbt 的 Clone() 返回 NbtTag，需要手动转型。
        /// </summary>
        private static T? CloneTag<T>(NbtCompound parent, string tagName) where T : NbtTag
        {
            var tag = parent[tagName];
            if (tag == null) return null;

            return tag.Clone() as T;
        }

        /// <summary>
        /// 克隆任意类型的 NBT 标签。
        /// </summary>
        private static NbtTag? CloneTag(NbtCompound parent, string tagName)
        {
            var tag = parent[tagName];
            return tag?.Clone() as NbtTag;
        }

        /// <summary>
        /// 在目标 compound 中替换指定标签（先移除旧的，再添加新的）。
        /// 如果 newTag 为 null，则不做任何操作（保留目标中的原始值）。
        /// </summary>
        private static void ReplaceTag(NbtCompound target, NbtTag? newTag)
        {
            if (newTag == null) return;

            var tagName = newTag.Name;
            if (string.IsNullOrEmpty(tagName)) return;

            // 移除已有同名标签
            if (target.Contains(tagName))
            {
                target.Remove(tagName);
            }

            target.Add(newTag);
        }

        #endregion
    }
}
