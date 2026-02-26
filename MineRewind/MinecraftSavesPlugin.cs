using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
    public partial class MinecraftSavesPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider, IFolderRewindKnotLinkCommandHandler
    {
        #region 常量

        private const string ConfigTypeName = "Minecraft Saves";
        private const string HotBackupSettingKey = "EnableHotBackup";
        private const string PreservePlayerDataSettingKey = "PreservePlayerData";
        private static readonly string[] RequiredFilterEntries = { "session.lock", "voxy" };

        private const string Hotkey_ActiveWorldHotBackup = "hotbackup.active_world";
        private const string Hotkey_QuickRestore = "hotrestore.active_world";

        private const string KnotLinkCommand_BackupCurrent = "BACKUP_CURRENT";
        private const string KnotLinkCommand_RestoreCurrentLatest = "RESTORE_CURRENT_LATEST";
        private const string KnotLinkCommand_ListBackupsCurrent = "LIST_BACKUPS_CURRENT";
        private const string KnotLinkCommand_RestoreCurrent = "RESTORE_CURRENT";

        // 伪装版本：联动模组只认 MineBackup 1.13.0+
        private const string FakeVersion = "1.13.1";
        private const string MinModVersion = "1.1.0";

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

        // 宿主上下文（持久缓存，用于主动发起 KnotLink 操作）
        private PluginHostContext? _hostContext;

        // 仅用于 BACKUP_CURRENT：强制在差异检测前执行一次热备份协同保存
        private readonly ConcurrentDictionary<string, byte> _forceHotBackupFolders
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
            Version = "1.5.0",
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
            MinHostVersion = "1.4.2",
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
                    DisplayName = I18n.GetString("MineRewind_Setting_EnableHotBackup_Name"),
                    Description = I18n.GetString("MineRewind_Setting_EnableHotBackup_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = PreservePlayerDataSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_PreservePlayerData_Name"),
                    Description = I18n.GetString("MineRewind_Setting_PreservePlayerData_Desc"),
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

        #endregion
    }
}
