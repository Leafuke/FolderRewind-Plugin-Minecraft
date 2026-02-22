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
    }
}
