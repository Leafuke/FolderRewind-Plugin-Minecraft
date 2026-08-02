using FolderRewind.Services.KnotLink;
using FolderRewind.Services.Plugins;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        public PluginKnotLinkCapabilityContribution GetKnotLinkCapabilities()
        {
            var openSocket = new List<PluginKnotLinkOpenSocketCapability>
            {
                Capability("backup_current", "Backup the currently active Minecraft world.", "BACKUP", true,
                    ("from", KnotLinkFuncListService.Input("Required caller identifier.", "minebackup.mod")),
                    ("request_id", KnotLinkFuncListService.Input("Required request ID.", "request-001")),
                    ("comment", KnotLinkFuncListService.Input("Optional backup comment.", "QuickSave")),
                    ("backup_mode", KnotLinkFuncListService.Optional(
                        "Optional one-shot backup mode.",
                        ("Full", "full"),
                        ("Incremental", "incremental"))),
                    ("compression_method", KnotLinkFuncListService.Optional(
                        "Optional one-shot compression method.",
                        ("LZMA2", "LZMA2"),
                        ("Deflate", "Deflate"),
                        ("BZip2", "BZip2"),
                        ("zstd", "zstd"))),
                    ("compression_level", KnotLinkFuncListService.Input("Optional one-shot compression level.", ""))),
                Capability("list_backups_current", "List backups for the currently active Minecraft world.", "LIST_BACKUPS", true),
                Capability("restore_current_latest", "Restore the active Minecraft world from its latest backup.", "RESTORE", true,
                    ("from", KnotLinkFuncListService.Input("Required caller identifier.", "minebackup.mod")),
                    ("request_id", KnotLinkFuncListService.Input("Required request ID.", "request-001"))),
                Capability("restore_current", "Restore the active Minecraft world from a specified backup.", "RESTORE", true,
                    ("from", KnotLinkFuncListService.Input("Required caller identifier.", "minebackup.mod")),
                    ("request_id", KnotLinkFuncListService.Input("Required request ID.", "request-001")),
                    ("file", KnotLinkFuncListService.Input("Backup archive file name.", "backup.7z"))),
                Capability("restore_current_with_data", "Restore the active world while preserving player data.", "RESTORE", true,
                    ("from", KnotLinkFuncListService.Input("Required caller identifier.", "minebackup.mod")),
                    ("request_id", KnotLinkFuncListService.Input("Required request ID.", "request-001")),
                    ("preserve_player_data", KnotLinkFuncListService.Static("true", "Preserve inventory and player position.")),
                    ("file", KnotLinkFuncListService.Input("Optional backup archive; empty means latest.", ""))),
                Capability("handshake_response", "Report the companion mod version.", "HANDSHAKE_RESPONSE", false,
                    ("mod_version", KnotLinkFuncListService.Input("Companion mod version.", "1.0.0"))),
                Capability("world_saved", "Report that the current world was saved.", "WORLD_SAVED", false),
                Capability("world_save_and_exit_complete", "Report that save-and-exit completed.", "WORLD_SAVE_AND_EXIT_COMPLETE", false),
                Capability("rejoin_result", "Report the automatic world rejoin result.", "REJOIN_RESULT", false,
                    ("result", KnotLinkFuncListService.Optional("Rejoin result.", ("Success", "success"), ("Failure", "failure"))),
                    ("reason", KnotLinkFuncListService.Input("Optional failure reason.", "")))
            };

            var signal = new List<PluginKnotLinkSignalCapability>
            {
                SignalCapability("handshake", "Request a companion mod handshake.",
                    ("version", "Main application compatibility version."),
                    ("action", "Requested coordination action: backup or restore."),
                    ("world", "Minecraft world name."),
                    ("min_mod_version", "Minimum compatible companion mod version.")),
                SignalCapability("handshake_ack", "Acknowledge a companion mod handshake.",
                    ("status", "Compatibility status.")),
                SignalCapability("pre_hot_backup", "Ask the companion mod to save before hot backup.",
                    ("world", "Minecraft world name.")),
                SignalCapability("pre_hot_restore", "Ask the companion mod to save and exit before restore.",
                    ("world", "Minecraft world name.")),
                SignalCapability("restore_cancelled", "Report that hot restore was cancelled.",
                    ("reason", "Cancellation reason.")),
                SignalCapability("rejoin_world", "Ask the companion mod to rejoin a restored world.",
                    ("world", "Minecraft world name.")),
                SignalCapability("hot_restore_complete", "Report hot restore completion.",
                    ("status", "Restore result status."))
            };

            return new PluginKnotLinkCapabilityContribution { OpenSocket = openSocket, Signal = signal };
        }

        private static PluginKnotLinkOpenSocketCapability Capability(
            string name,
            string description,
            string command,
            bool currentSave,
            params (string Name, KnotLinkFuncArgument Argument)[] extraArgs)
        {
            var args = new Dictionary<string, KnotLinkFuncArgument>(StringComparer.Ordinal)
            {
                ["cmd"] = KnotLinkFuncListService.Static(command, "Operation command.")
            };
            if (currentSave) args["current_save"] = KnotLinkFuncListService.Static("true", "Target the currently active world.");
            foreach (var (argName, argument) in extraArgs) args[argName] = argument;
            return new PluginKnotLinkOpenSocketCapability
            {
                Name = name,
                Description = description,
                Args = args,
                Returns = KnotLinkFuncListService.StatusReturns("message")
            };
        }

        private static PluginKnotLinkSignalCapability SignalCapability(
            string name,
            string description,
            params (string Name, string Description)[] fields)
        {
            var returns = new Dictionary<string, KnotLinkSignalField>(StringComparer.Ordinal)
            {
                ["event"] = new() { Description = "Signal event name.", Verification = name }
            };
            foreach (var field in fields)
            {
                returns[field.Name] = new() { Description = field.Description };
            }

            return new PluginKnotLinkSignalCapability
            {
                Name = name,
                Description = description,
                Returns = returns
            };
        }

    }
}
