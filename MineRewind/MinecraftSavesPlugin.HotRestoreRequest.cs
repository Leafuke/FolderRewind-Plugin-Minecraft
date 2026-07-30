using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Threading;

namespace MineRewind
{
    public partial class MinecraftSavesPlugin
    {
        public async Task<PluginRestoreInterceptionResult> TryInterceptRestoreAsync(
            BackupConfig config,
            ManagedFolder folder,
            string archiveFileName,
            IReadOnlyDictionary<string, string> settingsValues,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CanHandleConfigType(config.ConfigType))
            {
                return PluginRestoreInterceptionResult.Continue();
            }

            if (string.Equals(
                _hostContext?.CurrentKnotLinkCommandContext?.Command,
                "RESTORE",
                StringComparison.OrdinalIgnoreCase))
            {
                return PluginRestoreInterceptionResult.Continue();
            }

            var worldPath = MinecraftWorldPathResolver.TryResolveWorldPath(folder.Path);
            if (worldPath == null || !IsWorldOccupied(worldPath))
            {
                return PluginRestoreInterceptionResult.Continue();
            }

            try
            {
                return await RequestHotRestoreAsync(
                    archiveFileName,
                    _hostContext,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string failedMessage = Localize("MineRewind_HotRestore_BroadcastFailed");
                LogService.LogError(failedMessage, "MineRewind", ex);
                return PluginRestoreInterceptionResult.Blocked(failedMessage);
            }
        }

        private async Task<PluginRestoreInterceptionResult> RequestHotRestoreAsync(
            string? archiveFileName,
            PluginHostContext? hostContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            var requestId = Guid.NewGuid();
            var fields = MinecraftHotRestoreProtocol.BuildRequestFields(normalizedFile, requestId);
            bool sent = await hostContext.TryBroadcastEventAsync(
                null,
                "hot_restore_requested",
                fields).ConfigureAwait(false);
            if (!sent)
            {
                string failedMessage = Localize("MineRewind_HotRestore_BroadcastFailed");
                LogService.LogWarning(failedMessage, "MineRewind");
                return PluginRestoreInterceptionResult.Blocked(failedMessage);
            }

            string target = normalizedFile == null
                ? Localize("MineRewind_HotRestore_TargetLatest")
                : LocalizeFormat("MineRewind_HotRestore_TargetFile", normalizedFile);
            string acceptedMessage = LocalizeFormat(
                "MineRewind_HotRestore_Requested",
                target,
                requestId.ToString("D"));
            LogService.LogInfo(acceptedMessage, "MineRewind");
            return PluginRestoreInterceptionResult.Handled(acceptedMessage);
        }

    }
}
