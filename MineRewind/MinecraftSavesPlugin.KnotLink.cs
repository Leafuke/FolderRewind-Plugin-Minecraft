using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        #region KnotLink 指令扩展

        public IReadOnlyList<PluginKnotLinkCommandDefinition> GetKnotLinkCommandDefinitions()
        {
            return new List<PluginKnotLinkCommandDefinition>
            {
                new() { Command = KnotLinkCommand_BackupCurrent, Description = "Backup the currently running (occupied) Minecraft world" },
                new() { Command = KnotLinkCommand_RestoreCurrentLatest, Description = "Hot-restore the current world to its latest backup (requires mod)" },
                new() { Command = KnotLinkCommand_ListBackupsCurrent, Description = "List all backups for the currently active (occupied) world" },
                new() { Command = KnotLinkCommand_RestoreCurrent, Description = "Hot-restore the current world from a specified backup file (requires mod)" },
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
                "LIST_BACKUPS_CURRENT" => HandleListBackupsCurrentAsync(hostContext),
                "RESTORE_CURRENT" => HandleRestoreCurrentAsync(args, settingsValues, hostContext),
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
                        MarkForceHotBackup(folder.Path);
                        await BackupService.BackupFolderAsync(cfg, folder, string.IsNullOrWhiteSpace(args) ? "QuickSave" : args);
                    }
                    catch (Exception ex)
                    {
                        LogService.LogError(I18n.Format("MineRewind_KnotLink_BackupCurrent_Failed", ex.Message), "MineRewind", ex);
                        try { hostContext?.BroadcastEvent($"event=knotlink_backup_failed;plugin=minerewind;command={KnotLinkCommand_BackupCurrent};config={cfg.Id};world={Uri.EscapeDataString(folder.DisplayName ?? string.Empty)};error={Uri.EscapeDataString(ex.Message)}"); } catch { }
                    }
                    finally
                    {
                        ClearForceHotBackup(folder.Path);
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

        /// <summary>
        /// LIST_BACKUPS_CURRENT: 列出当前活跃世界的所有备份文件
        /// 对应 MineBackup Console.cpp 的 LIST_BACKUPS_CURRENT 指令
        /// </summary>
        private Task<string?> HandleListBackupsCurrentAsync(PluginHostContext hostContext)
        {
            try
            {
                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("event=knotlink_list_backups_no_active_world;plugin=minerewind"); } catch { }
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
                    hostContext?.BroadcastEvent(
                        $"event=list_backups_current;config={config.Id};world={Uri.EscapeDataString(worldName)};data={result}");
                }
                catch { }

                return Task.FromResult<string?>(result);
            }
            catch (Exception ex)
            {
                LogService.LogError($"LIST_BACKUPS_CURRENT failed: {ex.Message}", "MineRewind", ex);
                return Task.FromResult<string?>($"ERROR:{ex.Message}");
            }
        }

        /// <summary>
        /// RESTORE_CURRENT: 对当前活跃世界执行指定备份文件的热还原
        /// 对应 MineBackup Console.cpp 的 RESTORE_CURRENT 指令
        /// 参数 args 为备份文件名
        /// </summary>
        private async Task<string?> HandleRestoreCurrentAsync(string args, IReadOnlyDictionary<string, string> settingsValues, PluginHostContext hostContext)
        {
            try
            {
                var backupFile = args.Trim();
                if (string.IsNullOrWhiteSpace(backupFile))
                {
                    return "ERROR:Missing backup file. Usage: RESTORE_CURRENT <backup_filename>";
                }

                var active = TryFindOccupiedWorld();
                if (active == null)
                {
                    try { hostContext?.BroadcastEvent("event=knotlink_restore_no_active_world;plugin=minerewind"); } catch { }
                    return "ERROR:No active world.";
                }

                var (config, folder) = active.Value;
                _ = Task.Run(() => TriggerHotRestoreAsync(config, folder, backupFile));
                return $"OK:Hot restore triggered for '{folder.DisplayName}' with backup '{backupFile}'";
            }
            catch (Exception ex)
            {
                LogService.LogError($"RESTORE_CURRENT failed: {ex.Message}", "MineRewind", ex);
                return $"ERROR:{ex.Message}";
            }
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
                KnotLinkService.BroadcastEvent($"event=handshake_ack;status={status};mod_version={modVersion}");
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
            var parts = args.Trim().Split(' ', 2);
            var successStr = parts.Length > 0 ? parts[0].ToLowerInvariant() : "failure";
            var reason = parts.Length > 1 ? parts[1] : string.Empty;
            var success = successStr == "success";

            LogService.LogInfo($"Mod reports rejoin result: {successStr}, reason: {reason}", "MineRewind");
            _rejoinTcs?.TrySetResult((success, reason));
            return Task.FromResult<string?>($"OK:Rejoin result received ({successStr})");
        }

        #endregion
    }
}
