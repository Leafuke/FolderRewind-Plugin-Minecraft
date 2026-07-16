using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region KnotLink 指令扩展

        public Task<PluginParameterizedKnotLinkCommandResult?> TryHandleParameterizedKnotLinkCommandAsync(
            KnotLinkCommandRequest request,
            IReadOnlyDictionary<string, string> settingsValues,
            PluginHostContext hostContext)
        {
            Initialize(settingsValues);

            switch (request.Command)
            {
                case "HANDSHAKE_RESPONSE":
                    return WrapParameterizedResponseAsync(
                        HandleHandshakeResponseAsync(request.GetStringOrDefault("mod_version"), hostContext));
                case "WORLD_SAVED":
                    return WrapParameterizedResponseAsync(HandleWorldSavedAsync(hostContext));
                case "WORLD_SAVE_AND_EXIT_COMPLETE":
                    return WrapParameterizedResponseAsync(HandleWorldSaveAndExitCompleteAsync(hostContext));
                case "REJOIN_RESULT":
                    var result = request.GetStringOrDefault("result");
                    var reason = request.GetStringOrDefault("reason");
                    return WrapParameterizedResponseAsync(
                        HandleRejoinResultAsync(string.Join(' ', new[] { result, reason }.Where(value => !string.IsNullOrWhiteSpace(value))), hostContext));
            }

            if (!request.GetBoolOrDefault("current_save"))
            {
                return Task.FromResult<PluginParameterizedKnotLinkCommandResult?>(null);
            }

            return request.Command switch
            {
                "BACKUP" => WrapParameterizedResponseAsync(
                    HandleBackupCurrentAsync(BuildBackupCurrentArgs(request), settingsValues, hostContext)),
                "LIST_BACKUPS" => WrapParameterizedResponseAsync(
                    HandleListBackupsCurrentAsync(hostContext)),
                "RESTORE" => WrapParameterizedResponseAsync(
                    request.GetBoolOrDefault("preserve_player_data")
                        ? HandleRestoreCurrentWithDataAsync(BuildRestoreCurrentArgs(request), settingsValues, hostContext)
                        : string.IsNullOrWhiteSpace(BuildRestoreCurrentArgs(request))
                            ? HandleRestoreCurrentLatestAsync(string.Empty, settingsValues, hostContext)
                            : HandleRestoreCurrentAsync(BuildRestoreCurrentArgs(request), settingsValues, hostContext)),
                _ => Task.FromResult<PluginParameterizedKnotLinkCommandResult?>(null)
            };
        }

        private static async Task<PluginParameterizedKnotLinkCommandResult?> WrapParameterizedResponseAsync(Task<string?> handlerTask)
        {
            var response = await handlerTask.ConfigureAwait(false);
            return new PluginParameterizedKnotLinkCommandResult
            {
                Handled = true,
                Response = string.IsNullOrWhiteSpace(response) ? "OK:" : response
            };
        }

        private static string BuildBackupCurrentArgs(KnotLinkCommandRequest request)
        {
            var args = new List<string>();
            var comment = request.GetString("comment");
            if (!string.IsNullOrWhiteSpace(comment))
            {
                args.Add(comment);
            }

            if (request.GetBoolOrDefault("force_full"))
            {
                args.Add("FORCE_FULL");
            }

            return string.Join(' ', args);
        }

        private static string BuildRestoreCurrentArgs(KnotLinkCommandRequest request)
        {
            return request.GetString("file") ?? string.Empty;
        }

        private Task<string?> HandleBackupCurrentAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("knotlink_backup_no_active_world", new Dictionary<string, string?> { ["plugin"] = "minerewind" }); } catch { }
                    return Task.FromResult<string?>("ERROR:No active world.");
                }

                var (cfg, folder) = active.Value;
                var (comment, forceFullBackup) = ParseBackupArgsAndForceFull(args, "QuickSave");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunForcedHotBackupAsync(
                            cfg,
                            folder,
                            comment,
                            forceFullBackup,
                            BackupInvocationOptions.ForRemote());
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(LocalizeFormat("MineRewind_KnotLink_BackupCurrent_Failed", ex.Message), "MineRewind", ex);
                        try
                        {
                            hostContext?.BroadcastEvent("knotlink_backup_failed", new Dictionary<string, string?>
                            {
                                ["plugin"] = "minerewind",
                                ["command"] = "BACKUP",
                                ["config"] = cfg.Id,
                                ["world"] = folder.DisplayName,
                                ["error"] = ex.Message
                            });
                        }
                        catch { }
                    }
                });

                return Task.FromResult<string?>($"OK:Backup started for '{folder.DisplayName}'");
            }
            catch (Exception ex)
            {
                LogService.LogError(LocalizeFormat("MineRewind_KnotLink_BackupCurrent_Failed", ex.Message), "MineRewind", ex);
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
                    try { hostContext?.BroadcastEvent("knotlink_restore_no_active_world", new Dictionary<string, string?> { ["plugin"] = "minerewind" }); } catch { }
                    return "ERROR:No active world.";
                }

                var (config, folder) = active.Value;
                _ = Task.Run(() => TriggerHotRestoreAsync(config, folder));
                return $"OK:Hot restore triggered for '{folder.DisplayName}'";
            }
            catch (Exception ex)
            {
                LogService.LogError($"RESTORE current_save latest failed: {ex.Message}", "MineRewind", ex);
                return $"ERROR:{ex.Message}";
            }
        }

        /// <summary>
        /// LIST_BACKUPS + current_save: 列出当前活跃世界的所有备份文件
        /// </summary>
        private Task<string?> HandleListBackupsCurrentAsync(PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("knotlink_list_backups_no_active_world", new Dictionary<string, string?> { ["plugin"] = "minerewind" }); } catch { }
                    return Task.FromResult<string?>("ERROR:No active world found.");
                }

                var (config, folder) = active.Value;
                var worldName = folder.DisplayName ?? Path.GetFileName(folder.Path);
                var backupDir = Path.Combine(config.DestinationPath, worldName);

                var sb = new System.Text.StringBuilder("OK:");
                if (Directory.Exists(backupDir))
                {
                    var extensions = new[] { ".7z", ".zip" };
                    var files = Directory.GetFiles(backupDir)
                        .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Select(Path.GetFileName);

                    foreach (var file in files)
                    {
                        sb.Append(file);
                        sb.Append(';');
                    }
                }

                // 移除末尾分号
                if (sb.Length > 3 && sb[sb.Length - 1] == ';')
                    sb.Length--;

                var result = sb.ToString();

                try
                {
                    hostContext?.BroadcastEvent("list_backups_current", new Dictionary<string, string?>
                    {
                        ["config"] = config.Id,
                        ["world"] = worldName,
                        ["data"] = result
                    });
                }
                catch { }

                return Task.FromResult<string?>(result);
            }
            catch (Exception ex)
            {
                LogService.LogError($"LIST_BACKUPS current_save failed: {ex.Message}", "MineRewind", ex);
                return Task.FromResult<string?>($"ERROR:{ex.Message}");
            }
        }

        /// <summary>
        /// RESTORE + current_save + file: 对当前活跃世界执行指定备份文件的热还原
        /// 参数 args 为备份文件名
        /// </summary>
        private async Task<string?> HandleRestoreCurrentAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var backupFile = args.Trim();
                if (string.IsNullOrWhiteSpace(backupFile))
                {
                    return "ERROR:Missing backup file. Usage: cmd=RESTORE;current_save=true;file=backup.7z";
                }

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("knotlink_restore_no_active_world", new Dictionary<string, string?> { ["plugin"] = "minerewind" }); } catch { }
                    return "ERROR:No active world.";
                }

                var (config, folder) = active.Value;
                _ = Task.Run(() => TriggerHotRestoreAsync(config, folder, backupFile));
                return $"OK:Hot restore triggered for '{folder.DisplayName}' with backup '{backupFile}'";
            }
            catch (Exception ex)
            {
                LogService.LogError($"RESTORE current_save failed: {ex.Message}", "MineRewind", ex);
                return $"ERROR:{ex.Message}";
            }
        }

        /// <summary>
        /// RESTORE + current_save + preserve_player_data: 热还原并保留玩家数据。
        /// 参数 args 可选，为备份文件名；省略时使用最新备份。
        /// </summary>
        private async Task<string?> HandleRestoreCurrentWithDataAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("knotlink_restore_no_active_world", new Dictionary<string, string?> { ["plugin"] = "minerewind" }); } catch { }
                    return "ERROR:No active world.";
                }

                var (config, folder) = active.Value;
                var backupFile = string.IsNullOrWhiteSpace(args) ? null : args.Trim();

                _ = Task.Run(() => TriggerHotRestoreAsync(config, folder, backupFile, forcePreservePlayerData: true));
                var fileInfo = backupFile != null ? $" with backup '{backupFile}'" : " (latest)";
                return $"OK:Hot restore with data preservation triggered for '{folder.DisplayName}'{fileInfo}";
            }
            catch (Exception ex)
            {
                LogService.LogError($"RESTORE current_save preserve_player_data failed: {ex.Message}", "MineRewind", ex);
                return $"ERROR:{ex.Message}";
            }
        }

        private static (string Comment, bool ForceFullBackup) ParseBackupArgsAndForceFull(string args, string defaultComment)
        {
            if (string.IsNullOrWhiteSpace(args))
                return (defaultComment, false);

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool forceFullBackup = false;
            var commentParts = new List<string>(parts.Length);

            foreach (var part in parts)
            {
                if (string.Equals(part, "FORCE_FULL", StringComparison.OrdinalIgnoreCase))
                {
                    forceFullBackup = true;
                    continue;
                }

                commentParts.Add(part);
            }

            var comment = string.Join(' ', commentParts).Trim();
            if (string.IsNullOrWhiteSpace(comment))
                comment = defaultComment;

            return (comment, forceFullBackup);
        }

        private Task<string?> HandleHandshakeResponseAsync(string args, PluginHostContext hostContext)
        {
            if (string.IsNullOrWhiteSpace(args))
                return Task.FromResult<string?>("ERROR:Missing mod version. Usage: HANDSHAKE_RESPONSE <mod_version>");

            var pendingHandshake = _handshakeTcs;
            if (pendingHandshake == null)
            {
                LogService.LogWarning("Received HANDSHAKE_RESPONSE with no pending handshake, ignored.", "MineRewind");
                return Task.FromResult<string?>("ERROR:No pending handshake.");
            }

            var modVersion = args.Trim();
            _modDetected = true;
            _modVersion = modVersion;
            _versionCompatible = IsModVersionCompatible(modVersion, MinModVersion);

            LogService.LogInfo($"Mod detected: version {modVersion}, compatible={_versionCompatible}", "MineRewind");

            try
            {
                var status = _versionCompatible ? "compatible" : "incompatible";
                KnotLinkService.BroadcastEvent(null, "handshake_ack", new Dictionary<string, string?>
                {
                    ["status"] = status,
                    ["mod_version"] = modVersion
                });
            }
            catch { }

            pendingHandshake.TrySetResult(_versionCompatible);

            var compatStr = _versionCompatible ? "compatible" : "incompatible";
            return Task.FromResult<string?>($"OK:Handshake received. Version {modVersion} ({compatStr})");
        }

        private Task<string?> HandleWorldSavedAsync(PluginHostContext hostContext)
        {
            LogService.LogInfo("Mod reports: world save complete.", "MineRewind");
            _worldSaveTcs?.TrySetResult(true);
            return Task.FromResult<string?>("OK:World save acknowledged.");
        }

        private Task<string?> HandleWorldSaveAndExitCompleteAsync(PluginHostContext hostContext)
        {
            // 与 MineBackup 保持一致：仅在热还原等待模组阶段才接受此消息
            if (Interlocked.CompareExchange(ref _hotRestoreState, _hotRestoreState, RestoreWaitingForMod) != RestoreWaitingForMod)
            {
                return Task.FromResult<string?>("ERROR:Not currently waiting for a world save-and-exit signal.");
            }

            LogService.LogInfo("Mod reports: world saved and exited.", "MineRewind");
            _worldSaveAndExitTcs?.TrySetResult(true);
            return Task.FromResult<string?>("OK:Acknowledged. Restore will now proceed.");
        }

        private Task<string?> HandleRejoinResultAsync(string args, PluginHostContext hostContext)
        {
            if (string.IsNullOrWhiteSpace(args))
                return Task.FromResult<string?>("ERROR:Missing result. Usage: REJOIN_RESULT <success|failure> [reason]");

            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return Task.FromResult<string?>("ERROR:Missing result. Usage: REJOIN_RESULT <success|failure> [reason]");

            var successStr = parts[0].ToLowerInvariant();
            if (successStr != "success" && successStr != "failure")
                return Task.FromResult<string?>("ERROR:Invalid result. Usage: REJOIN_RESULT <success|failure> [reason]");

            var reason = parts.Length > 1 ? parts[1] : string.Empty;
            var success = successStr == "success";

            LogService.LogInfo($"Mod reports rejoin result: {successStr}, reason: {reason}", "MineRewind");
            _rejoinTcs?.TrySetResult((success, reason));
            return Task.FromResult<string?>($"OK:Rejoin result received ({successStr})");
        }

        #endregion
    }
}
