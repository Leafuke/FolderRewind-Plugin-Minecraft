using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
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
    public class MinecraftSavesPlugin : IFolderRewindPlugin, IFolderRewindHotkeyProvider, IFolderRewindKnotLinkCommandHandler
    {
        #region 常量

        private const string ConfigTypeName = "Minecraft Saves";
        private const string HotBackupSettingKey = "EnableHotBackup";
        private const string SnapshotPathSettingKey = "SnapshotPath";
        private const string CleanupSnapshotSettingKey = "CleanupSnapshot";
        private const string SnapshotDelaySettingKey = "SnapshotDelayMs";

        private const string Hotkey_ActiveWorldHotBackup = "hotbackup.active_world";
        private const string Hotkey_QuickRestore = "hotrestore.active_world";

        private const string KnotLinkCommand_BackupCurrent = "BACKUP_CURRENT";
        private const string KnotLinkCommand_RestoreCurrentLatest = "RESTORE_CURRENT_LATEST";

        // 伪装版本：联动模组只认 MineBackup 1.13.0+
        private const string FakeVersion = "1.13.0";
        private const string MinModVersion = "1.0.0";

        // 超时常量（参考 MineBackup C++ 实现）
        private const int HandshakeTimeoutMs = 100;
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
        private string _snapshotPath = string.Empty;
        private bool _cleanupSnapshot = true;
        private int _snapshotDelayMs = 500;

        // 临时快照路径映射: 原始路径 -> 快照路径
        private readonly Dictionary<string, string> _activeSnapshots = new();

        // 宿主上下文（持久缓存，用于主动发起 KnotLink 操作）
        private PluginHostContext? _hostContext;

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

        public PluginInstallManifest Manifest { get; } = new()
        {
            Id = "com.folderrewind.minerewind",
            Name = "MineRewind",
            Version = "1.2.0",
            Author = "Leafuke",
            Description = "Enhanced Minecraft saves backup: hot snapshot backup, batch discovery under .minecraft",
            LocalizedName = new Dictionary<string, string>
            {
                ["zh-CN"] = "MineRewind",
                ["en-US"] = "MineRewind",
            },
            LocalizedDescription = new Dictionary<string, string>
            {
                ["zh-CN"] = "Minecraft 存档备份增强插件：支持热备份、批量扫描 .minecraft 目录、自动发现存档，以及全局热键触发备份",
                ["en-US"] = "Enhanced Minecraft saves backup: hot snapshot backup, batch discovery under .minecraft, plus a global hotkey trigger",
            },
            EntryAssembly = "MineRewind.dll",
            EntryType = "MineRewind.MinecraftSavesPlugin",
            MinHostVersion = "1.1.0",
            Repository = "Leafuke/FolderRewind-Plugin-Minecraft"
        };

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
                    Key = SnapshotPathSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_SnapshotPath_Name"),
                    Description = I18n.GetString("MineRewind_Setting_SnapshotPath_Desc"),
                    Type = PluginSettingType.Path,
                    DefaultValue = "",
                    IsRequired = false
                },
                new()
                {
                    Key = CleanupSnapshotSettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_CleanupSnapshot_Name"),
                    Description = I18n.GetString("MineRewind_Setting_CleanupSnapshot_Desc"),
                    Type = PluginSettingType.Boolean,
                    DefaultValue = "true",
                    IsRequired = false
                },
                new()
                {
                    Key = SnapshotDelaySettingKey,
                    DisplayName = I18n.GetString("MineRewind_Setting_SnapshotDelay_Name"),
                    Description = I18n.GetString("MineRewind_Setting_SnapshotDelay_Desc"),
                    Type = PluginSettingType.Integer,
                    DefaultValue = "500",
                    IsRequired = false
                }
            };
        }

        public void Initialize(IReadOnlyDictionary<string, string> settingsValues)
        {
            _enableHotBackup = GetBoolSetting(settingsValues, HotBackupSettingKey, true);
            _snapshotPath = GetStringSetting(settingsValues, SnapshotPathSettingKey, string.Empty);
            _cleanupSnapshot = GetBoolSetting(settingsValues, CleanupSnapshotSettingKey, true);
            _snapshotDelayMs = GetIntSetting(settingsValues, SnapshotDelaySettingKey, 500);
        }

        /// <summary>
        /// 接收宿主上下文（由 Host 在加载插件后注入）
        /// </summary>
        public void SetHostContext(PluginHostContext hostContext)
        {
            _hostContext = hostContext;
        }

        #region 配置类型与发现

        public IReadOnlyList<string> GetSupportedConfigTypes()
        {
            return new[] { ConfigTypeName };
        }

        public bool CanHandleConfigType(string configType)
        {
            return string.Equals(configType, ConfigTypeName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自动发现存档文件夹
        /// </summary>
        public IReadOnlyList<ManagedFolder> TryDiscoverManagedFolders(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var results = new List<ManagedFolder>();

            if (string.IsNullOrWhiteSpace(selectedRootPath) || !Directory.Exists(selectedRootPath))
                return results;

            var dirName = Path.GetFileName(selectedRootPath);

            // .minecraft
            if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromDotMinecraft(selectedRootPath));
            }
            // saves
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(DiscoverFromSavesDirectory(selectedRootPath));
            }
            // versions
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                results.AddRange(DiscoverFromSavesDirectory(Path.Combine(selectedRootPath, "saves")));
            }
            // 存档（包含level.dat）
            else if (File.Exists(Path.Combine(selectedRootPath, "level.dat")))
            {
                results.Add(CreateManagedFolder(selectedRootPath));
            }

            return results;
        }

        /// <summary>
        /// 批量创建配置 - 扫描 .minecraft 目录结构
        /// </summary>
        public PluginCreateConfigResult TryCreateConfigs(string selectedRootPath, IReadOnlyDictionary<string, string> settingsValues)
        {
            var configs = new List<BackupConfig>();

            if (string.IsNullOrWhiteSpace(selectedRootPath) || !Directory.Exists(selectedRootPath))
            {
                return new PluginCreateConfigResult { Handled = false };
            }

            var dirName = Path.GetFileName(selectedRootPath);

            if (dirName.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
            {
                configs.AddRange(CreateConfigsFromDotMinecraft(selectedRootPath));
            }
            else if (Directory.Exists(Path.Combine(selectedRootPath, "saves")))
            {
                var config = CreateConfigForVersion(selectedRootPath);
                if (config != null)
                    configs.Add(config);
            }
            else if (dirName.Equals("saves", StringComparison.OrdinalIgnoreCase))
            {
                var parentDir = Directory.GetParent(selectedRootPath)?.FullName;
                var versionName = parentDir != null ? Path.GetFileName(parentDir) : "Unknown";
                var config = CreateConfigForSavesDir(selectedRootPath, versionName);
                if (config != null)
                    configs.Add(config);
            }

            if (configs.Count == 0)
            {
                return new PluginCreateConfigResult { Handled = false };
            }

            return new PluginCreateConfigResult
            {
                Handled = true,
                CreatedConfigs = configs,
                Message = I18n.Format("MineRewind_CreateConfigs_Result", configs.Count)
            };
        }

        #endregion

        #region 插件热键

        public IReadOnlyList<PluginHotkeyDefinition> GetHotkeyDefinitions()
        {
            return new List<PluginHotkeyDefinition>
            {
                new()
                {
                    Id = Hotkey_ActiveWorldHotBackup,
                    DisplayName = I18n.GetString("MineRewind_Hotkey_ActiveWorldBackup_Name"),
                    Description = I18n.GetString("MineRewind_Hotkey_ActiveWorldBackup_Desc"),
                    DefaultGesture = "Alt+Ctrl+S",
                    IsGlobalHotkey = true
                },
                new()
                {
                    Id = Hotkey_QuickRestore,
                    DisplayName = "快速还原当前存档",
                    Description = "将当前正在运行的 Minecraft 存档还原到最新备份（需要联动模组支持）",
                    DefaultGesture = "Alt+Ctrl+Z",
                    IsGlobalHotkey = true
                }
            };
        }

        public async Task OnHotkeyInvokedAsync(string hotkeyId, bool isGlobalHotkey, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            if (string.Equals(hotkeyId, Hotkey_ActiveWorldHotBackup, StringComparison.OrdinalIgnoreCase))
            {
                await HandleBackupHotkeyAsync(settingsValues, hostContext);
            }
            else if (string.Equals(hotkeyId, Hotkey_QuickRestore, StringComparison.OrdinalIgnoreCase))
            {
                await HandleRestoreHotkeyAsync(settingsValues, hostContext);
            }
        }

        /// <summary>
        /// 热备份热键处理
        /// </summary>
        private async Task HandleBackupHotkeyAsync(IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                Initialize(settingsValues);

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    LogService.LogInfo(I18n.GetString("MineRewind_Hotkey_NoActiveWorld"), "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_backup_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;

                try
                {
                    hostContext?.BroadcastEvent($"event=hotkey_backup_triggered;plugin=minerewind;config={config.Id};world={Uri.EscapeDataString(folder.DisplayName ?? string.Empty)}");
                }
                catch
                {
                }

                // 触发热备份：走 Host 的 BackupService 流程，MineRewind 的 OnBeforeBackupFolder 会自动创建快照
                await BackupService.BackupFolderAsync(config, folder, "[热键] MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MineRewind_Hotkey_Failed", ex.Message), "MineRewind", ex);
            }
        }

        /// <summary>
        /// 快速还原热键处理：将当前运行的世界还原到最新备份
        /// </summary>
        private async Task HandleRestoreHotkeyAsync(IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                Initialize(settingsValues);

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    LogService.LogInfo("快速还原：未检测到活跃存档", "MineRewind");
                    try { hostContext?.BroadcastEvent("event=hotkey_restore_no_active_world;plugin=minerewind"); } catch { }
                    return;
                }

                var (config, folder) = active.Value;
                await TriggerHotRestoreAsync(config, folder);
            }
            catch (Exception ex)
            {
                LogService.LogError($"快速还原热键失败: {ex.Message}", "MineRewind", ex);
            }
        }

        private static (BackupConfig config, ManagedFolder folder)? TryFindOccupiedWorld()
        {
            try
            {
                var configs = ConfigService.CurrentConfig?.BackupConfigs;
                if (configs == null) return null;

                foreach (var cfg in configs)
                {
                    if (cfg == null) continue;
                    if (!string.Equals(cfg.ConfigType, ConfigTypeName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cfg.SourceFolders == null) continue;

                    foreach (var folder in cfg.SourceFolders)
                    {
                        if (folder == null) continue;
                        if (string.IsNullOrWhiteSpace(folder.Path)) continue;
                        if (!Directory.Exists(folder.Path)) continue;

                        if (IsWorldOccupied(folder.Path))
                        {
                            return (cfg, folder);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool IsWorldOccupied(string worldPath)
        {
            try
            {
                // Java 版：session.lock
                var sessionLock = Path.Combine(worldPath, "session.lock");
                if (File.Exists(sessionLock) && IsFileLocked(sessionLock)) return true;

                // 基岩版：可能没有 session.lock，遍历 db 目录下的文件看看有没有被锁定
                var dbDir = Path.Combine(worldPath, "db");
                if (Directory.Exists(dbDir))
                {
                    foreach (var entry in Directory.EnumerateFiles(dbDir))
                    {
                        if (IsFileLocked(entry)) return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        #endregion

        #region KnotLink 指令扩展

        public IReadOnlyList<PluginKnotLinkCommandDefinition> GetKnotLinkCommandDefinitions()
        {
            return new List<PluginKnotLinkCommandDefinition>
            {
                new() { Command = KnotLinkCommand_BackupCurrent, Description = "Backup the currently running (occupied) Minecraft world" },
                new() { Command = KnotLinkCommand_RestoreCurrentLatest, Description = "Hot-restore the current world to its latest backup (requires mod)" },
                new() { Command = "HANDSHAKE_RESPONSE", Description = "Mod handshake response (internal)" },
                new() { Command = "WORLD_SAVED", Description = "Mod notifies world save complete (internal)" },
                new() { Command = "WORLD_SAVE_AND_EXIT_COMPLETE", Description = "Mod notifies world saved and exited (internal)" },
                new() { Command = "SHUTDOWN_WORLD_SUCCESS", Description = "Legacy: same as WORLD_SAVE_AND_EXIT_COMPLETE (internal)" },
                new() { Command = "REJOIN_RESULT", Description = "Mod reports rejoin world result (internal)" },
            };
        }

        public Task<string?> TryHandleKnotLinkCommandAsync(
            string command,
            string args,
            string rawCommand,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext)
        {
            Initialize(settingsValues);

            return command.ToUpperInvariant() switch
            {
                "BACKUP_CURRENT" => HandleBackupCurrentAsync(args, settingsValues, hostContext),
                "RESTORE_CURRENT_LATEST" => HandleRestoreCurrentLatestAsync(args, settingsValues, hostContext),
                "HANDSHAKE_RESPONSE" => HandleHandshakeResponseAsync(args, hostContext),
                "WORLD_SAVED" => HandleWorldSavedAsync(hostContext),
                "WORLD_SAVE_AND_EXIT_COMPLETE" => HandleWorldSaveAndExitCompleteAsync(hostContext),
                "SHUTDOWN_WORLD_SUCCESS" => HandleWorldSaveAndExitCompleteAsync(hostContext),
                "REJOIN_RESULT" => HandleRejoinResultAsync(args, hostContext),
                _ => Task.FromResult<string?>(null)
            };
        }

        private Task<string?> HandleBackupCurrentAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("event=knotlink_backup_no_active_world;plugin=minerewind"); } catch { }
                    return Task.FromResult<string?>("ERROR:No active world.");
                }

                var (cfg, folder) = active.Value;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        try { hostContext?.BroadcastEvent($"event=knotlink_backup_triggered;plugin=minerewind;command={KnotLinkCommand_BackupCurrent};config={cfg.Id};world={Uri.EscapeDataString(folder.DisplayName ?? string.Empty)}"); } catch { }
                        await BackupService.BackupFolderAsync(cfg, folder, string.IsNullOrWhiteSpace(args) ? "QuickSave" : args);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(I18n.Format("MineRewind_KnotLink_BackupCurrent_Failed", ex.Message), "MineRewind", ex);
                        try { hostContext?.BroadcastEvent($"event=knotlink_backup_failed;plugin=minerewind;command={KnotLinkCommand_BackupCurrent};config={cfg.Id};world={Uri.EscapeDataString(folder.DisplayName ?? string.Empty)};error={Uri.EscapeDataString(ex.Message)}"); } catch { }
                    }
                });

                return Task.FromResult<string?>($"OK:Backup started for '{folder.DisplayName}'");
            }
            catch (Exception ex)
            {
                LogService.LogError(I18n.Format("MineRewind_KnotLink_BackupCurrent_Failed", ex.Message), "MineRewind", ex);
                return Task.FromResult<string?>($"ERROR:{ex.Message}");
            }
        }

        private async Task<string?> HandleRestoreCurrentLatestAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("event=knotlink_restore_no_active_world;plugin=minerewind"); } catch { }
                    return "ERROR:No active world.";
                }

                var (config, folder) = active.Value;
                _ = Task.Run(() => TriggerHotRestoreAsync(config, folder));
                return $"OK:Hot restore triggered for '{folder.DisplayName}'";
            }
            catch (Exception ex)
            {
                LogService.LogError($"RESTORE_CURRENT_LATEST failed: {ex.Message}", "MineRewind", ex);
                return $"ERROR:{ex.Message}";
            }
        }

        private Task<string?> HandleHandshakeResponseAsync(string args, PluginHostContext hostContext)
        {
            if (string.IsNullOrWhiteSpace(args))
                return Task.FromResult<string?>("ERROR:Missing mod version. Usage: HANDSHAKE_RESPONSE <mod_version>");

            var modVersion = args.Trim();
            _modDetected = true;
            _modVersion = modVersion;
            _versionCompatible = IsModVersionCompatible(modVersion, MinModVersion);

            LogService.LogInfo($"Mod detected: version {modVersion}, compatible={_versionCompatible}", "MineRewind");

            try
            {
                var status = _versionCompatible ? "compatible" : "incompatible";
                KnotLinkService.BroadcastEvent($"event=handshake_ack;status={status};mod_version={modVersion}");
            }
            catch { }

            _handshakeTcs?.TrySetResult(_versionCompatible);

            var compatStr = _versionCompatible ? "compatible" : "incompatible";
            return Task.FromResult<string?>($"OK:Handshake received. Version {modVersion} ({compatStr})");
        }

        private Task<string?> HandleWorldSavedAsync(PluginHostContext hostContext)
        {
            LogService.LogInfo("Mod reports: world save complete.", "MineRewind");
            try { KnotLinkService.BroadcastEvent("event=world_save_acknowledged;"); } catch { }
            _worldSaveTcs?.TrySetResult(true);
            return Task.FromResult<string?>("OK:World save acknowledged.");
        }

        private Task<string?> HandleWorldSaveAndExitCompleteAsync(PluginHostContext hostContext)
        {
            LogService.LogInfo("Mod reports: world saved and exited.", "MineRewind");
            _worldSaveAndExitTcs?.TrySetResult(true);
            return Task.FromResult<string?>("OK:World save and exit acknowledged.");
        }

        private Task<string?> HandleRejoinResultAsync(string args, PluginHostContext hostContext)
        {
            var parts = args.Trim().Split(' ', 2);
            var successStr = parts.Length > 0 ? parts[0].ToLowerInvariant() : "failure";
            var reason = parts.Length > 1 ? parts[1] : string.Empty;
            var success = successStr == "success";

            LogService.LogInfo($"Mod reports rejoin result: {successStr}, reason: {reason}", "MineRewind");
            _rejoinTcs?.TrySetResult((success, reason));
            return Task.FromResult<string?>($"OK:Rejoin result received ({successStr})");
        }

        #endregion

        #region 备份钩子 - 热备份

        /// <summary>
        /// 备份前钩子：创建热备份快照（支持与联动模组协调）
        /// </summary>
        public string? OnBeforeBackupFolder(BackupConfig config, ManagedFolder folder, IReadOnlyDictionary<string, string> settingsValues)
        {
            // 重新读取设置
            Initialize(settingsValues);

            // 仅对 Minecraft Saves 类型的配置启用热备份
            if (!CanHandleConfigType(config.ConfigType))
                return null;

            if (!_enableHotBackup)
                return null;

            // 检查是否存在 level.dat (Minecraft 存档的标志文件)
            var levelDatPath = Path.Combine(folder.Path, "level.dat");
            bool isMinecraftSave = File.Exists(levelDatPath);

            if (!isMinecraftSave)
                return null;

            // 检查文件是否被锁定
            bool isLocked = IsFileLocked(levelDatPath);

            // 若世界正在运行且 KnotLink 可用，尝试与模组协调
            if (isLocked && KnotLinkService.IsEnabled && KnotLinkService.IsInitialized)
            {
                try
                {
                    var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);

                    // 发起握手（伪装为 MineBackup 1.13.0）
                    var handshakeOk = PerformModHandshakeSync("backup", worldName);

                    if (handshakeOk && _modDetected && _versionCompatible)
                    {
                        // 握手成功，通知模组进行保存
                        Thread.Sleep(PostHandshakeDelayMs);

                        KnotLinkService.BroadcastEvent(
                            $"event=pre_hot_backup;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                        // 等待 WORLD_SAVED
                        _worldSaveTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var saved = _worldSaveTcs.Task.Wait(WorldSaveTimeoutMs);
                        if (saved && _worldSaveTcs.Task.Result)
                        {
                            LogService.LogInfo($"Mod confirmed world save for '{worldName}'", "MineRewind");
                        }
                        else
                        {
                            LogService.LogWarning($"WORLD_SAVED timed out for '{worldName}', proceeding with snapshot anyway", "MineRewind");
                        }
                    }
                    else
                    {
                        LogService.LogInfo("No compatible mod detected, creating snapshot directly", "MineRewind");
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning($"Mod coordination failed: {ex.Message}, proceeding with snapshot", "MineRewind");
                }
            }

            // 创建快照
            try
            {
                var snapshotPath = CreateSnapshot(folder.Path);
                if (!string.IsNullOrWhiteSpace(snapshotPath))
                {
                    _activeSnapshots[folder.Path] = snapshotPath;

                    if (isLocked)
                    {
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Locked", folder.DisplayName),
                            "MineRewind");
                    }
                    else
                    {
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Created", folder.DisplayName),
                            "MineRewind");
                    }

                    return snapshotPath;
                }
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("MineRewind_Snapshot_CreateFailed", ex.Message),
                    "MineRewind",
                    ex);
            }

            return null;
        }

        /// <summary>
        /// 备份后钩子：清理快照
        /// </summary>
        public void OnAfterBackupFolder(BackupConfig config, ManagedFolder folder, bool success, string? generatedArchiveFileName, IReadOnlyDictionary<string, string> settingsValues)
        {
            // 重新读取设置
            Initialize(settingsValues);

            if (!_cleanupSnapshot)
                return;

            // 清理快照
            if (_activeSnapshots.TryGetValue(folder.Path, out var snapshotPath))
            {
                _activeSnapshots.Remove(folder.Path);

                try
                {
                    if (Directory.Exists(snapshotPath))
                    {
                        Directory.Delete(snapshotPath, recursive: true);
                        LogService.LogInfo(
                            I18n.Format("MineRewind_Snapshot_Cleaned", folder.DisplayName),
                            "MineRewind");
                    }
                }
                catch (Exception ex)
                {
                    LogService.LogWarning(
                        I18n.Format("MineRewind_Snapshot_CleanupFailed", ex.Message),
                        "MineRewind");
                }
            }
        }

        #endregion

        #region 私有方法 - 目录发现

        private IEnumerable<ManagedFolder> DiscoverFromDotMinecraft(string dotMinecraftPath)
        {
            var results = new List<ManagedFolder>();

            // .minecraft/saves
            var directSaves = Path.Combine(dotMinecraftPath, "saves");
            if (Directory.Exists(directSaves))
            {
                results.AddRange(DiscoverFromSavesDirectory(directSaves));
            }

            // .minecraft/versions/*/saves (多版本隔离模式)
            var versionsDir = Path.Combine(dotMinecraftPath, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                {
                    var versionSaves = Path.Combine(versionDir, "saves");
                    if (Directory.Exists(versionSaves))
                    {
                        results.AddRange(DiscoverFromSavesDirectory(versionSaves));
                    }
                }
            }

            return results;
        }

        private IEnumerable<ManagedFolder> DiscoverFromSavesDirectory(string savesPath)
        {
            var results = new List<ManagedFolder>();

            if (!Directory.Exists(savesPath))
                return results;

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                // 验证是否为有效的 Minecraft 存档 (包含 level.dat)
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    results.Add(CreateManagedFolder(worldDir));
                }
            }

            return results;
        }

        private ManagedFolder CreateManagedFolder(string worldPath)
        {
            var worldName = Path.GetFileName(worldPath);
            var coverImage = FindCoverImage(worldPath);

            return new ManagedFolder
            {
                Path = worldPath,
                DisplayName = worldName,
                Description = GetWorldDescription(worldPath),
                CoverImagePath = coverImage
            };
        }

        private string? FindCoverImage(string worldPath)
        {
            // Minecraft 存档的封面图片通常在 icon.png
            var iconPath = Path.Combine(worldPath, "icon.png");
            if (File.Exists(iconPath))
                return iconPath;

            return null;
        }

        private string GetWorldDescription(string worldPath)
        {
            try
            {
                var levelDat = Path.Combine(worldPath, "level.dat");
                if (File.Exists(levelDat))
                {
                    var fileInfo = new FileInfo(levelDat);
                    var lastModified = fileInfo.LastWriteTime;
                    return $"最后修改: {lastModified:yyyy/MM/dd HH:mm}";
                }
            }
            catch { }

            return "Minecraft 存档";
        }

        #endregion

        #region 私有方法 - 配置创建

        private IEnumerable<BackupConfig> CreateConfigsFromDotMinecraft(string dotMinecraftPath)
        {
            var configs = new List<BackupConfig>();
            var versionSavesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            // 收集直接 saves 目录的存档 (归入 "Default" 版本)
            var directSaves = Path.Combine(dotMinecraftPath, "saves");
            if (Directory.Exists(directSaves))
            {
                var worlds = Directory.EnumerateDirectories(directSaves)
                    .Where(d => File.Exists(Path.Combine(d, "level.dat")))
                    .ToList();

                if (worlds.Count > 0)
                {
                    versionSavesMap["Default"] = worlds;
                }
            }

            // versions/*/saves
            var versionsDir = Path.Combine(dotMinecraftPath, "versions");
            if (Directory.Exists(versionsDir))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
                {
                    var versionName = Path.GetFileName(versionDir);
                    var versionSaves = Path.Combine(versionDir, "saves");

                    if (Directory.Exists(versionSaves))
                    {
                        var worlds = Directory.EnumerateDirectories(versionSaves)
                            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
                            .ToList();

                        if (worlds.Count > 0)
                        {
                            versionSavesMap[versionName] = worlds;
                        }
                    }
                }
            }

            // 为每个版本创建一个配置
            foreach (var kvp in versionSavesMap)
            {
                var config = new BackupConfig
                {
                    Name = $"Minecraft - {kvp.Key}",
                    ConfigType = ConfigTypeName,
                    IconGlyph = "\uE7FC", // 游戏图标
                };

                // 添加存档文件夹
                foreach (var worldPath in kvp.Value)
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldPath));
                }

                config.ExtendedProperties["MinecraftVersion"] = kvp.Key;
                config.ExtendedProperties["Plugin"] = Manifest.Id;

                configs.Add(config);
            }

            return configs;
        }

        private BackupConfig? CreateConfigForVersion(string versionDirPath)
        {
            var versionName = Path.GetFileName(versionDirPath);
            var savesPath = Path.Combine(versionDirPath, "saves");

            if (!Directory.Exists(savesPath))
                return null;

            var config = new BackupConfig
            {
                Name = $"Minecraft - {versionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC",
            };

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldDir));
                }
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            config.ExtendedProperties["Plugin"] = Manifest.Id;

            return config;
        }

        private BackupConfig? CreateConfigForSavesDir(string savesPath, string versionName)
        {
            if (!Directory.Exists(savesPath))
                return null;

            var config = new BackupConfig
            {
                Name = $"Minecraft - {versionName}",
                ConfigType = ConfigTypeName,
                IconGlyph = "\uE7FC",
            };

            foreach (var worldDir in Directory.EnumerateDirectories(savesPath))
            {
                if (File.Exists(Path.Combine(worldDir, "level.dat")))
                {
                    config.SourceFolders.Add(CreateManagedFolder(worldDir));
                }
            }

            if (config.SourceFolders.Count == 0)
                return null;

            config.ExtendedProperties["MinecraftVersion"] = versionName;
            config.ExtendedProperties["Plugin"] = Manifest.Id;

            return config;
        }

        #endregion

        #region 私有方法 - 热备份

        /// <summary>
        /// 使用 xcopy 创建存档快照
        /// </summary>
        private string? CreateSnapshot(string sourcePath)
        {
            try
            {
                var worldName = Path.GetFileName(sourcePath);

                // 确定快照目录
                string snapshotBaseDir;
                if (!string.IsNullOrWhiteSpace(_snapshotPath) && Directory.Exists(_snapshotPath))
                {
                    snapshotBaseDir = Path.Combine(_snapshotPath, "FolderRewind_Snapshot");
                }
                else
                {
                    snapshotBaseDir = Path.Combine(Path.GetTempPath(), "FolderRewind_Snapshot");
                }

                var snapshotDir = Path.Combine(snapshotBaseDir, worldName);

                // 如果旧快照存在，先清理
                if (Directory.Exists(snapshotDir))
                {
                    try
                    {
                        Directory.Delete(snapshotDir, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        // 如果无法删除旧快照，使用时间戳创建新目录，避免失败
                        LogService.LogWarning(
                            I18n.Format("MineRewind_Snapshot_CleanupOldFailed", ex.Message),
                            "MineRewind");
                        snapshotDir = Path.Combine(snapshotBaseDir, $"{worldName}_{DateTime.Now:yyyyMMdd_HHmmss}");
                    }
                }

                Directory.CreateDirectory(snapshotDir);

                // 使用 xcopy 复制 (忽略错误继续)
                // /s: 复制子目录
                // /e: 复制空目录
                // /y: 覆盖确认
                // /c: 忽略错误继续
                // /i: 目标是目录
                var xcopyArgs = $"\"{sourcePath}\" \"{snapshotDir}\" /s /e /y /c /i";

                var psi = new ProcessStartInfo
                {
                    FileName = "xcopy",
                    Arguments = xcopyArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    LogService.LogError(
                        I18n.GetString("MineRewind_Xcopy_StartFailed"),
                        "MineRewind");
                    return null;
                }

                // 先异步读取输出，避免死锁（当缓冲区填满时WaitForExit会永久阻塞）
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                // 等待完成（最多 120 秒）
                var completed = process.WaitForExit(120000);

                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    LogService.LogError(
                        I18n.GetString("MineRewind_Xcopy_Timeout"),
                        "MineRewind");
                    return null;
                }

                var stdOut = stdOutTask.GetAwaiter().GetResult();
                var stdErr = stdErrTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    LogService.LogWarning(
                        I18n.Format("MineRewind_Xcopy_Stderr", stdErr),
                        "MineRewind");
                }

                // 等待文件系统操作完成
                if (_snapshotDelayMs > 0)
                {
                    System.Threading.Thread.Sleep(_snapshotDelayMs);
                }

                // 验证快照是否创建成功
                if (Directory.Exists(snapshotDir))
                {
                    return snapshotDir;
                }

                LogService.LogWarning(
                    I18n.GetString("MineRewind_Snapshot_VerifyFailed"),
                    "MineRewind");
                return null;
            }
            catch (Exception ex)
            {
                LogService.LogError(
                    I18n.Format("MineRewind_Snapshot_Exception", ex.Message),
                    "MineRewind",
                    ex);
                return null;
            }
        }

        /// <summary>
        /// 检查文件是否被锁定
        /// </summary>
        private static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 文件可能是只读或没有权限
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 辅助方法

        private static bool GetBoolSetting(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue)
        {
            if (settings.TryGetValue(key, out var value))
            {
                if (bool.TryParse(value, out var result))
                    return result;

                // 支持 "1" / "0" 格式
                if (value == "1") return true;
                if (value == "0") return false;
            }
            return defaultValue;
        }

        private static string GetStringSetting(IReadOnlyDictionary<string, string> settings, string key, string defaultValue)
        {
            if (settings.TryGetValue(key, out var value))
                return value ?? defaultValue;
            return defaultValue;
        }

        private static int GetIntSetting(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
        {
            if (settings.TryGetValue(key, out var value) && int.TryParse(value, out var result))
                return result;
            return defaultValue;
        }

        #endregion

        #region 模组握手与热还原

        /// <summary>
        /// 执行模组握手（同步版本，供 OnBeforeBackupFolder 使用）
        /// </summary>
        private bool PerformModHandshakeSync(string action, string worldName)
        {
            return PerformModHandshakeAsync(action, worldName, HandshakeTimeoutMs)
                .GetAwaiter().GetResult();
        }

        /// <summary>
        /// 执行模组握手（异步版本）
        /// 伪装为 MineBackup 1.13.0 发送握手广播，等待模组的 HANDSHAKE_RESPONSE
        /// </summary>
        private async Task<bool> PerformModHandshakeAsync(string action, string worldName, int timeoutMs = HandshakeTimeoutMs)
        {
            _modDetected = false;
            _modVersion = string.Empty;
            _versionCompatible = false;
            _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var payload = $"event=handshake;version={FakeVersion};action={action};world={Uri.EscapeDataString(worldName)};min_mod_version={MinModVersion}";

            try
            {
                await KnotLinkService.BroadcastEventAsync(payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Handshake broadcast failed: {ex.Message}", "MineRewind");
                return false;
            }

            try
            {
                var delayTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(_handshakeTcs.Task, delayTask).ConfigureAwait(false);

                if (completedTask == _handshakeTcs.Task)
                {
                    return _handshakeTcs.Task.Result;
                }
            }
            catch { }

            LogService.LogInfo("Mod handshake timed out, no mod detected.", "MineRewind");
            return false;
        }

        /// <summary>
        /// 触发热还原流程（参考 MineBackup 的 TriggerHotkeyRestore / DoHotRestore）
        /// </summary>
        private async Task TriggerHotRestoreAsync(BackupConfig config, ManagedFolder folder)
        {
            // CAS: IDLE → WAITING_FOR_MOD
            if (Interlocked.CompareExchange(ref _hotRestoreState, RestoreWaitingForMod, RestoreIdle) != RestoreIdle)
            {
                LogService.LogWarning("Hot restore already in progress, ignoring request.", "MineRewind");
                return;
            }

            var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);

            try
            {
                LogService.LogInfo($"Starting hot restore for '{worldName}'...", "MineRewind");

                // 1. 握手
                var handshakeOk = await PerformModHandshakeAsync("restore", worldName);
                if (!handshakeOk || !_modDetected || !_versionCompatible)
                {
                    LogService.LogWarning("Hot restore requires a compatible mod. Aborting.", "MineRewind");
                    KnotLinkService.BroadcastEvent(
                        $"event=restore_cancelled;reason=no_mod;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                // 2. 短暂延迟（握手和下一个广播之间，参考 MineBackup）
                await Task.Delay(PostHandshakeDelayMs);

                // 3. 广播 pre_hot_restore，通知模组保存并退出世界
                _worldSaveAndExitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=pre_hot_restore;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                // 4. 等待 WORLD_SAVE_AND_EXIT_COMPLETE
                LogService.LogInfo("Waiting for mod to save and exit world...", "MineRewind");
                var exitTask = await Task.WhenAny(_worldSaveAndExitTcs.Task, Task.Delay(WorldExitTimeoutMs));
                if (exitTask != _worldSaveAndExitTcs.Task || !_worldSaveAndExitTcs.Task.Result)
                {
                    LogService.LogWarning("World save and exit timed out, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_cancelled;reason=timeout;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                // 5. 等待文件系统释放世界文件
                LogService.LogInfo("Waiting for world files to be released...", "MineRewind");
                if (!await WaitForWorldReleaseAsync(folder.Path, FileReleaseTimeoutMs))
                {
                    LogService.LogWarning("World files still occupied after timeout, cancelling restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_cancelled;reason=world_occupied;world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                // 6. 等待 level.dat 文件锁释放
                var levelDat = Path.Combine(folder.Path, "level.dat");
                if (File.Exists(levelDat))
                {
                    if (!await WaitForFileUnlockedAsync(levelDat, LevelDatReleaseTimeoutMs, LevelDatCheckIntervalMs))
                    {
                        LogService.LogWarning("level.dat still locked, cancelling restore.", "MineRewind");
                        await KnotLinkService.BroadcastEventAsync(
                            $"event=restore_cancelled;reason=world_occupied;world={Uri.EscapeDataString(worldName)}");
                        return;
                    }
                }

                // 7. 额外等待文件系统同步
                await Task.Delay(500);

                // 8. 状态 → RESTORING
                Interlocked.Exchange(ref _hotRestoreState, RestoreRestoring);

                // 9. 查找最新备份
                var latestBackup = FindLatestBackupFileName(config, folder);
                if (string.IsNullOrEmpty(latestBackup))
                {
                    LogService.LogError($"No backup found for '{worldName}', aborting restore.", "MineRewind");
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                // 10. 执行还原
                LogService.LogInfo($"Restoring '{worldName}' from '{latestBackup}'...", "MineRewind");
                try
                {
                    await BackupService.RestoreBackupAsync(config, folder, latestBackup);
                }
                catch (Exception ex)
                {
                    LogService.LogError($"Restore failed: {ex.Message}", "MineRewind", ex);
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                    return;
                }

                // 11. 广播还原成功
                await Task.Delay(100);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=restore_finished;status=success;config={config.Id};world={Uri.EscapeDataString(worldName)}");

                // 12. 等待世界文件系统稳定
                await Task.Delay(PostRestoreStabilizeMs);

                // 13. 通知模组重新进入世界
                _rejoinTcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
                await KnotLinkService.BroadcastEventAsync(
                    $"event=rejoin_world;world={Uri.EscapeDataString(worldName)}");

                // 14. 等待 REJOIN_RESULT
                LogService.LogInfo("Waiting for mod to rejoin world...", "MineRewind");
                string hotRestoreStatus;
                var rejoinTask = await Task.WhenAny(_rejoinTcs.Task, Task.Delay(RejoinTimeoutMs));

                if (rejoinTask == _rejoinTcs.Task)
                {
                    var (success, reason) = _rejoinTcs.Task.Result;
                    hotRestoreStatus = success ? "full_success" : "restore_ok_rejoin_failed";
                    if (!success)
                    {
                        LogService.LogWarning($"Rejoin failed: {reason}", "MineRewind");
                    }
                }
                else
                {
                    hotRestoreStatus = "restore_ok_rejoin_timeout";
                    LogService.LogWarning("Rejoin world timed out.", "MineRewind");
                }

                // 15. 广播热还原完成状态
                await KnotLinkService.BroadcastEventAsync(
                    $"event=hot_restore_complete;status={hotRestoreStatus};world={Uri.EscapeDataString(worldName)}");

                LogService.LogInfo($"Hot restore completed: {hotRestoreStatus}", "MineRewind");
            }
            catch (Exception ex)
            {
                LogService.LogError($"Hot restore error: {ex.Message}", "MineRewind", ex);
                try
                {
                    await KnotLinkService.BroadcastEventAsync(
                        $"event=restore_finished;status=failure;config={config.Id};world={Uri.EscapeDataString(worldName)}");
                }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _hotRestoreState, RestoreIdle);
            }
        }

        /// <summary>
        /// 等待世界文件不再被占用
        /// </summary>
        private static async Task<bool> WaitForWorldReleaseAsync(string worldPath, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsWorldOccupied(worldPath))
                    return true;

                await Task.Delay(FileLockCheckIntervalMs);
            }
            return false;
        }

        /// <summary>
        /// 等待指定文件不再被锁定
        /// </summary>
        private static async Task<bool> WaitForFileUnlockedAsync(string filePath, int timeoutMs, int intervalMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsFileLocked(filePath))
                    return true;

                await Task.Delay(intervalMs);
            }
            return false;
        }

        /// <summary>
        /// 查找指定配置和文件夹的最新备份文件名
        /// </summary>
        private static string? FindLatestBackupFileName(BackupConfig config, ManagedFolder folder)
        {
            try
            {
                var backupDir = Path.Combine(config.DestinationPath, folder.DisplayName ?? string.Empty);
                if (!Directory.Exists(backupDir))
                    return null;

                var extensions = new[] { ".7z", ".zip" };
                var files = Directory.GetFiles(backupDir)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(Path.GetFileName)
                    .FirstOrDefault();

                return files;
            }
            catch (Exception ex)
            {
                LogService.LogWarning($"Failed to find latest backup: {ex.Message}", "MineRewind");
                return null;
            }
        }

        /// <summary>
        /// 语义版本比较（参考 MineBackup 的 IsVersionCompatible）
        /// </summary>
        private static bool IsModVersionCompatible(string currentVer, string requiredVer)
        {
            try
            {
                var current = ParseVersion(currentVer);
                var required = ParseVersion(requiredVer);
                return current.CompareTo(required) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static (int Major, int Minor, int Patch) ParseVersion(string v)
        {
            var parts = v.Split('.');
            int major = parts.Length > 0 && int.TryParse(parts[0], out var ma) ? ma : 0;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
            int patch = parts.Length > 2 && int.TryParse(parts[2], out var pa) ? pa : 0;
            return (major, minor, patch);
        }

        #endregion
    }
}
