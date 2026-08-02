using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        public Task<PluginRestoreInterceptionResult> TryInterceptRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            IReadOnlyDictionary<string, string> settingsValues,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanHandleConfigType(config.ConfigType))
            {
                return Task.FromResult(PluginRestoreInterceptionResult.Continue());
            }

            if (string.Equals(
                _hostContext?.CurrentKnotLinkCommandContext?.Command,
                "RESTORE",
                StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(PluginRestoreInterceptionResult.Continue());
            }

            var worldPath = MinecraftWorldPathResolver.TryResolveWorldPath(folder.Path);
            if (worldPath == null || !IsWorldOccupied(worldPath))
            {
                return Task.FromResult(PluginRestoreInterceptionResult.Continue());
            }

            try
            {
                return Task.FromResult(RequestHotRestore(
                    config,
                    folder,
                    archiveFileName,
                    _hostContext));
            }
            catch (Exception ex)
            {
                string failedMessage = Localize("MineRewind_HotRestore_StartFailed");
                LogService.LogError(failedMessage, "MineRewind", ex);
                return Task.FromResult(PluginRestoreInterceptionResult.Blocked(failedMessage));
            }
        }

        private PluginRestoreInterceptionResult RequestHotRestore(
            BackupConfig config,
            ManagedFolder folder,
            string? archiveFileName,
            PluginHostContext? hostContext)
        {
            if (!MinecraftHotRestoreProtocol.TryNormalizeBackupId(archiveFileName, out var normalizedFile))
            {
                string invalidMessage = Localize("MineRewind_HotRestore_InvalidBackupId");
                LogService.LogWarning(invalidMessage, "MineRewind");
                return PluginRestoreInterceptionResult.Blocked(invalidMessage);
            }

            if (hostContext == null
                || !hostContext.IsKnotLinkAvailable
                || !hostContext.IsKnotLinkSenderReady)
            {
                string unavailableMessage = Localize("MineRewind_HotRestore_ChannelUnavailable");
                LogService.LogWarning(unavailableMessage, "MineRewind");
                return PluginRestoreInterceptionResult.Blocked(unavailableMessage);
            }

            if (!TryStartHotRestore(
                config,
                folder,
                normalizedFile,
                forcePreservePlayerData: false,
                out _))
            {
                string busyMessage = Localize("MineRewind_HotRestore_Busy");
                LogService.LogWarning(busyMessage, "MineRewind");
                return PluginRestoreInterceptionResult.Blocked(busyMessage);
            }

            string target = normalizedFile == null
                ? Localize("MineRewind_HotRestore_TargetLatest")
                : LocalizeFormat("MineRewind_HotRestore_TargetFile", normalizedFile);
            string acceptedMessage = LocalizeFormat(
                "MineRewind_HotRestore_Started",
                target);
            LogService.LogInfo(acceptedMessage, "MineRewind");
            return PluginRestoreInterceptionResult.Handled(acceptedMessage);
        }

    }
}
