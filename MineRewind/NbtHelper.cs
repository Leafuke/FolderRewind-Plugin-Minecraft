using fNbt;
using FolderRewind.Services;

namespace MineRewind
{
    /// <summary>
    /// Minecraft NBT 工具类 —— 基于 fNbt 库。
    /// 用于在还原存档时读取/写回 level.dat 中的玩家数据，
    /// 从而实现"还原存档但保留玩家位置和物品栏"等功能。
    /// 
    /// Minecraft level.dat 结构（单人存档）：
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
        /// 从 level.dat 中读取玩家数据快照。
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
                var player = data?["Player"] as NbtCompound;

                if (player == null)
                {
                    LogService.LogInfo("[NbtHelper] No Player compound in level.dat (server save or new world?).", "MineRewind");
                    return null;
                }

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
            catch (Exception ex)
            {
                LogService.LogError($"[NbtHelper] Failed to extract player data: {ex.Message}", "MineRewind", ex);
                return null;
            }
        }

        /// <summary>
        /// 将先前提取的玩家数据写回 level.dat。
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

            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
            {
                LogService.LogWarning("[NbtHelper] Restored level.dat not found, cannot apply player data.", "MineRewind");
                return false;
            }

            try
            {
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
                    // 如果还原后的 level.dat 没有 Player 节点（不太可能，但防御性处理）
                    LogService.LogWarning("[NbtHelper] No 'Player' compound in restored level.dat, creating one.", "MineRewind");
                    player = new NbtCompound("Player");
                    data.Add(player);
                }

                // 写回位置
                ReplaceTag(player, snapshot.Pos);
                ReplaceTag(player, snapshot.Rotation);
                ReplaceTag(player, snapshot.Dimension);

                // 写回物品栏
                ReplaceTag(player, snapshot.Inventory);
                ReplaceTag(player, snapshot.EnderItems);

                // 写回状态
                ReplaceTag(player, snapshot.XpLevel);
                ReplaceTag(player, snapshot.XpP);
                ReplaceTag(player, snapshot.XpTotal);
                ReplaceTag(player, snapshot.Score);
                ReplaceTag(player, snapshot.PlayerGameType);
                ReplaceTag(player, snapshot.Health);
                ReplaceTag(player, snapshot.FoodLevel);
                ReplaceTag(player, snapshot.FoodSaturationLevel);

                // 以 GZip 压缩保存回 level.dat
                nbtFile.SaveToFile(levelDatPath, NbtCompression.GZip);

                LogService.LogInfo("[NbtHelper] Player data successfully applied to restored level.dat.", "MineRewind");
                return true;
            }
            catch (Exception ex)
            {
                LogService.LogError($"[NbtHelper] Failed to apply player data: {ex.Message}", "MineRewind", ex);
                return false;
            }
        }

        /// <summary>
        /// 读取 level.dat 并返回存档的基本信息（用于 UI 展示/预览）。
        /// </summary>
        public static (string? LevelName, string? GameMode, long? DayTime, long? LastPlayed)? GetWorldInfo(string worldPath)
        {
            var levelDatPath = Path.Combine(worldPath, "level.dat");
            if (!File.Exists(levelDatPath))
                return null;

            try
            {
                var nbtFile = new NbtFile();
                nbtFile.LoadFromFile(levelDatPath);

                var data = nbtFile.RootTag["Data"] as NbtCompound;
                if (data == null) return null;

                var levelName = (data["LevelName"] as NbtString)?.Value;

                int? gameType = (data["GameType"] as NbtInt)?.Value;
                string? gameMode = gameType switch
                {
                    0 => "Survival",
                    1 => "Creative",
                    2 => "Adventure",
                    3 => "Spectator",
                    _ => null
                };

                long? dayTime = (data["DayTime"] as NbtLong)?.Value;
                long? lastPlayed = (data["LastPlayed"] as NbtLong)?.Value;

                return (levelName, gameMode, dayTime, lastPlayed);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"[NbtHelper] Failed to read world info: {ex.Message}", "MineRewind");
                return null;
            }
        }

        #region 私有辅助方法

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
