using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace MineRewind
{
    /// <summary>
    /// Minecraft 存档增强插件
    /// 功能：
    /// 1. 热备份 - 使用 xcopy 创建快照避免文件占用，支持与联动模组协调
    /// 2. 热还原 - 通过联动模组实现不退出游戏的快速还原
    /// 3. 批量扫描 - 自动发现 .minecraft 目录下的存档
    /// 4. 配置类型 - 定义 "Minecraft Saves" 类型
    /// 5. KnotLink 互联 - 与 MineBackup 联动模组握手、协调备份/还原
    /// </summary>
    public partial class MinecraftSavesPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider, IFolderRewindKnotLinkCommandHandler, IFolderRewindParameterizedKnotLinkCommandHandler, IFolderRewindBackupScopeProvider, IFolderRewindFolderDetailsProvider
    {
        #region 常量

        private const string ConfigTypeName = "Minecraft Saves";
        private const string HotBackupSettingKey = "EnableHotBackup";
        private const string PreservePlayerDataSettingKey = "PreservePlayerData";
        private static readonly string[] RequiredFilterEntries = { "session.lock", "voxy", "DistantHorizons.sqlite", "DistantHorizons.sqlite-shm", "DistantHorizons.sqlite-wal" };

        private const string Hotkey_ActiveWorldHotBackup = "hotbackup.active_world";
        private const string Hotkey_QuickRestore = "hotrestore.active_world";

        private const string KnotLinkCommand_BackupCurrent = "BACKUP_CURRENT";
        private const string KnotLinkCommand_RestoreCurrentLatest = "RESTORE_CURRENT_LATEST";
        private const string KnotLinkCommand_ListBackupsCurrent = "LIST_BACKUPS_CURRENT";
        private const string KnotLinkCommand_RestoreCurrent = "RESTORE_CURRENT";
        private const string KnotLinkCommand_RestoreCurrentWithData = "RESTORE_CURRENT_WITH_DATA";

        // 伪装版本：联动模组只认 MineBackup 1.14.0+
        private const string FakeVersion = "1.15.0";
        private const string MinModVersion = "2.1.1";

        // 超时常量（参考 MineBackup C++ 实现）
        private const int HandshakeTimeoutMs = 3_000;
        private const int WorldSaveTimeoutMs = 10_000;      // 等待 WORLD_SAVED: 10s
        private const int WorldExitTimeoutMs = 10_000;       // 等待 WORLD_SAVE_AND_EXIT_COMPLETE: 10s
        private const int FileReleaseTimeoutMs = 15_000;     // 等待文件释放: 15s
        private const int FileLockCheckIntervalMs = 500;     // 文件释放检测间隔
        private const int LevelDatReleaseTimeoutMs = 10_000; // level.dat 锁释放: 10s
        private const int LevelDatCheckIntervalMs = 200;
        private const int RejoinTimeoutMs = 30_000;          // 等待 REJOIN_RESULT: 30s
        private const int PostHandshakeDelayMs = 100;        // 握手到下个广播的延迟
        private const int PostRestoreStabilizeMs = 3_000;    // 还原后等待文件系统稳定

        // 热还原状态
        private const int RestoreIdle = 0;
        private const int RestoreWaitingForMod = 1;
        private const int RestoreRestoring = 2;

        #endregion

        #region 私有字段

        private bool _enableHotBackup = true;
        private bool _preservePlayerData = false;

        // 当 RESTORE_CURRENT_WITH_DATA 指令触发时，强制下一次还原保留玩家数据，
        // 不管插件设置 PreservePlayerData 是否开启。
        private volatile bool _forcePreserveNextRestore = false;

        // 宿主上下文（持久缓存，用于主动发起 KnotLink 操作）
        private PluginHostContext? _hostContext;

        // 用于 BACKUP_CURRENT/热键备份：在差异检测前强制执行一次热备协同保存
        // 使用引用计数，避免并发触发时提前清除标记。
        private readonly ConcurrentDictionary<string, int> _forceHotBackupFolders
            = new(StringComparer.OrdinalIgnoreCase);

        // --- 联动模组状态 ---
        private volatile bool _modDetected;
        private string _modVersion = string.Empty;
        private volatile bool _versionCompatible;

        // 同步信号
        private TaskCompletionSource<bool>? _handshakeTcs;
        private TaskCompletionSource<bool>? _worldSaveTcs;
        private TaskCompletionSource<bool>? _worldSaveAndExitTcs;
        private TaskCompletionSource<(bool Success, string Reason)>? _rejoinTcs;

        // 热还原状态（原子操作）
        private volatile int _hotRestoreState = RestoreIdle;

        #endregion

        #region 插件清单与初始化

        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.folderrewind.minerewind",
            Name = "MineRewind",
            Version = "1.7.0",
            Author = "Leafuke",
            Description = "Enhanced Minecraft saves backup: lock-friendly backup, batch discovery under .minecraft",
            LocalizedName = new Dictionary<string, string>
            {
                ["zh-CN"] = "MineRewind",
                ["en-US"] = "MineRewind",
            },
            LocalizedDescription = new Dictionary<string, string>
            {
                ["zh-CN"] = "Minecraft 存档备份增强插件：支持热备份、批量扫描 .minecraft 目录、自动发现存档，以及全局热键触发备份",
                ["en-US"] = "Enhanced Minecraft saves backup: lock-friendly backup, batch discovery under .minecraft, plus a global hotkey trigger",
            },
            EntryAssembly = "MineRewind.dll",
            EntryType = "MineRewind.MinecraftSavesPlugin",
            MinHostVersion = "1.7.3",
            Repository = "Leafuke/FolderRewind-Plugin-Minecraft"
        };

        #endregion

        #region 插件设置定义

        public IReadOnlyList<PluginSettingDefinition> GetSettingsDefinitions()
        {
            return new List<PluginSettingDefinition>
            {
                new()
                {
                    Key = HotBackupSettingKey,
                    DisplayName = Localize("MineRewind_Setting_EnableHotBackup_Name"),
                    Description = Localize("MineRewind_Setting_EnableHotBackup_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = PreservePlayerDataSettingKey,
                    DisplayName = Localize("MineRewind_Setting_PreservePlayerData_Name"),
                    Description = Localize("MineRewind_Setting_PreservePlayerData_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "false",
                    IsRequired = false
                }
            };
        }

        #endregion

        #region 设置读取与宿主上下文

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
        {
            _enableHotBackup = GetBoolSetting(settingsValues, HotBackupSettingKey, true);
            _preservePlayerData = GetBoolSetting(settingsValues, PreservePlayerDataSettingKey, false);

            if (EnsureExistingMinecraftConfigFilters())
            {
                ConfigService.Save();
            }
        }

        /// <summary>
        /// 接收宿主上下文（由 Host 在加载插件后注入）
        /// </summary>
        public void SetHostContext(PluginHostContext hostContext)
        {
            _hostContext = hostContext;
        }

        private static bool EnsureRequiredFilters(BackupConfig config)
        {
            if (config == null)
                return false;

            config.Filters ??= new FilterSettings();
            config.Filters.Blacklist ??= new ObservableCollection<string>();
            config.Filters.RestoreWhitelist ??= new ObservableCollection<string>();

            bool changed = false;
            foreach (var entry in RequiredFilterEntries)
            {
                if (!config.Filters.Blacklist.Any(x => string.Equals(x?.Trim(), entry, StringComparison.OrdinalIgnoreCase)))
                {
                    config.Filters.Blacklist.Add(entry);
                    changed = true;
                }

                if (!config.Filters.RestoreWhitelist.Any(x => string.Equals(x?.Trim(), entry, StringComparison.OrdinalIgnoreCase)))
                {
                    config.Filters.RestoreWhitelist.Add(entry);
                    changed = true;
                }
            }

            return changed;
        }

        private bool EnsureExistingMinecraftConfigFilters()
        {
            var allConfigs = ConfigService.CurrentConfig?.BackupConfigs;
            if (allConfigs == null || allConfigs.Count == 0)
                return false;

            bool changed = false;
            foreach (var config in allConfigs)
            {
                if (config == null || !CanHandleConfigType(config.ConfigType))
                    continue;

                if (EnsureRequiredFilters(config))
                {
                    changed = true;
                }
            }

            return changed;
        }

        private static string FormatModInteropValue(string? value)
        {
            // MineBackup 联动协议使用原始 UTF-8 文本；对 world 做 URL 编码会导致模组无法匹配当前世界。
            return value ?? string.Empty;
        }

        #endregion

        #region Folder details

        public Task<IReadOnlyList<FolderDetailsSection>> GetFolderDetailsSectionsAsync(
            BackupConfig config,
            ManagedFolder folder,
            IReadOnlyDictionary<string, string> settingsValues,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (folder == null || string.IsNullOrWhiteSpace(folder.Path))
            {
                return Task.FromResult<IReadOnlyList<FolderDetailsSection>>(Array.Empty<FolderDetailsSection>());
            }

            var details = NbtHelper.TryGetWorldDetails(folder.Path);
            if (details == null)
            {
                return Task.FromResult<IReadOnlyList<FolderDetailsSection>>(Array.Empty<FolderDetailsSection>());
            }

            var section = new FolderDetailsSection
            {
                Title = Localize("MineRewind_Details_SectionTitle"),
                Items =
                {
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_WorldName"), Value = details.LevelName },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_GameMode"), Value = details.GameMode },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_Seed"), Value = details.Seed },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_WorldDays"), Value = FormatWorldDays(details.TotalTime) },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_TotalTime"), Value = FormatWorldTicks(details.TotalTime) },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_LastPlayed"), Value = FormatLastPlayed(details.LastPlayed) },
                    new FolderDetailsItem { Label = Localize("MineRewind_Details_PlayerData"), Value = details.HasPlayerData ? Localize("MineRewind_Details_Yes") : Localize("MineRewind_Details_No") }
                }
            };

            return Task.FromResult<IReadOnlyList<FolderDetailsSection>>([section]);
        }

        private static string FormatWorldTicks(long? totalTime)
        {
            if (totalTime is not long ticks)
            {
                return string.Empty;
            }

            try
            {
                // 20 ticks ≈ 1 秒，基于世界时间换算，非精确人类游玩时长
                return TimeSpan.FromSeconds(ticks / 20.0).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string FormatWorldDays(long? totalTime)
        {
            if (totalTime is not long ticks)
            {
                return string.Empty;
            }

            // Minecraft 每 24000 ticks 为一个游戏日天
            double days = ticks / 24000.0;
            return days.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string FormatLastPlayed(long? lastPlayed)
        {
            if (lastPlayed is not long unixEpochMs)
            {
                return string.Empty;
            }

            try
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixEpochMs).ToLocalTime();
                return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion
    }
}
