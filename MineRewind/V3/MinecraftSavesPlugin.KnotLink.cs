using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private const string InteropHostVersion = "1.16.0";
    private const string MinimumModVersion = "3.0.0";
    private const int HandshakeTimeoutMs = 3_000;
    private const int WorldSaveTimeoutMs = 10_000;
    private const int WorldExitTimeoutMs = 10_000;
    private const int WorldReleaseTimeoutMs = 15_000;
    private const int PostMutationSignalDelayMs = 100;
    private const int PostRestoreStabilizeMs = 3_000;
    private const int RejoinTimeoutMs = 30_000;

    private readonly SemaphoreSlim _handshakeGate = new(1, 1);
    private readonly SemaphoreSlim _worldSaveGate = new(1, 1);
    private readonly SemaphoreSlim _restoreSignalGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _preservePlayerDataVersionIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> _preservePlayerDataQuickFolders = new();
    private TaskCompletionSource<bool>? _pendingHandshake;
    private TaskCompletionSource<bool>? _pendingWorldSaved;
    private TaskCompletionSource<bool>? _pendingWorldExited;
    private TaskCompletionSource<bool>? _pendingRejoin;

    private async ValueTask<PluginCommandResult> ExecuteInboundKnotLinkAsync(
        string command,
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        switch (command.ToUpperInvariant())
        {
            case "HANDSHAKE_RESPONSE":
                return await AcknowledgeHandshakeAsync(arguments, context).ConfigureAwait(false);
            case "WORLD_SAVED":
                return Acknowledge(Volatile.Read(ref _pendingWorldSaved), "World save acknowledged.");
            case "WORLD_SAVE_AND_EXIT_COMPLETE":
                return Acknowledge(Volatile.Read(ref _pendingWorldExited), "World save-and-exit acknowledged.");
            case "REJOIN_RESULT":
                var rejoined = !TryGetValue(arguments, "result", out var result)
                               || !string.Equals(result, "failure", StringComparison.OrdinalIgnoreCase);
                var pendingRejoin = Volatile.Read(ref _pendingRejoin);
                if (pendingRejoin is null || !pendingRejoin.TrySetResult(rejoined))
                    return CommandFailure("minerewind.knotlink_ack_not_expected");
                return Success("Rejoin result acknowledged.");
            case "BACKUP":
                return await RequestCurrentBackupAsync(arguments, context).ConfigureAwait(false);
            case "LIST_BACKUPS":
                return await ListCurrentBackupsAsync(context).ConfigureAwait(false);
            case "RESTORE":
                return await RequestCurrentRestoreAsync(arguments, context).ConfigureAwait(false);
            default:
                return CommandFailure("minerewind.knotlink_command_unavailable");
        }
    }

    private async ValueTask<PluginCommandResult> AcknowledgeHandshakeAsync(
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        var pending = Volatile.Read(ref _pendingHandshake);
        if (pending is null || !TryGetValue(arguments, "mod_version", out var modVersion))
            return CommandFailure("minerewind.knotlink_handshake_not_expected");

        var compatible = IsCompatibleModVersion(modVersion, MinimumModVersion);
        try
        {
            await context.HostServices.KnotLink.SendAsync(
                "handshake_ack",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = compatible ? "compatible" : "incompatible",
                    ["mod_version"] = modVersion
                },
                context.OperationCancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.HostServices.Logger.Log(
                DiagnosticSeverity.Warning,
                "Failed to send the MineRewind handshake acknowledgement.",
                ex);
        }
        if (!pending.TrySetResult(compatible))
            return CommandFailure("minerewind.knotlink_handshake_not_expected");
        return Success($"Handshake received from mod {modVersion}.");
    }

    private async ValueTask<bool> PerformModHandshakeAsync(
        string action,
        string worldName,
        PluginInvocationContext context)
    {
        await _handshakeGate.WaitAsync(context.OperationCancellation).ConfigureAwait(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingHandshake, pending);
        try
        {
            await context.HostServices.KnotLink.SendAsync(
                "handshake",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["version"] = InteropHostVersion,
                    ["action"] = action,
                    ["world"] = worldName,
                    ["min_mod_version"] = MinimumModVersion
                },
                context.OperationCancellation).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(HandshakeTimeoutMs),
                    context.OperationCancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingHandshake, null, pending);
            _handshakeGate.Release();
        }
    }

    private async ValueTask<bool> RequestWorldSaveForBackupAsync(
        BackupConsistencyRequest request,
        string sourcePath,
        PluginInvocationContext context)
    {
        await _worldSaveGate.WaitAsync(context.OperationCancellation).ConfigureAwait(false);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingWorldSaved, pending);
        try
        {
            await context.HostServices.KnotLink.SendAsync(
                "pre_hot_backup",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["config"] = request.Config.ConfigId,
                    ["folder_id"] = request.Folder.FolderId.ToString("D"),
                    ["world"] = Path.GetFileName(sourcePath)
                },
                context.OperationCancellation).ConfigureAwait(false);

            // 先注册 TCS 再广播，避免模组快速回包时丢失 WORLD_SAVED。
            try
            {
                return await pending.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(WorldSaveTimeoutMs),
                    context.OperationCancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingWorldSaved, null, pending);
            _worldSaveGate.Release();
        }
    }

    private async ValueTask<bool> RequestWorldExitAsync(
        RestoreCoordinatorRequest request,
        PluginInvocationContext context)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingWorldExited, pending);
        try
        {
            await context.HostServices.KnotLink.SendAsync(
                "pre_hot_restore",
                RestoreArguments(request),
                context.OperationCancellation).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(WorldExitTimeoutMs),
                    context.OperationCancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingWorldExited, null, pending);
        }
    }

    private static async ValueTask<bool> WaitForWorldReleaseAsync(
        string worldPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < WorldReleaseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSessionLockHeld(worldPath)) return true;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        return !IsSessionLockHeld(worldPath);
    }

    private async ValueTask ReportRestoreCancelledAsync(
        RestoreCoordinatorRequest request,
        string reason,
        PluginInvocationContext context)
    {
        try
        {
            var arguments = RestoreArguments(request).ToDictionary(pair => pair.Key, pair => pair.Value);
            arguments["reason"] = reason;
            await context.HostServices.KnotLink.SendAsync(
                "restore_cancelled",
                arguments,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.HostServices.Logger.Log(
                DiagnosticSeverity.Warning,
                "Failed to report the canceled hot restore through KnotLink.",
                ex);
        }
    }

    private async ValueTask ReportRestoreMutationFinishedAsync(
        RestoreCoordinatorRequest request,
        OperationOutcome outcome,
        PluginInvocationContext context)
    {
        try
        {
            await Task.Delay(PostMutationSignalDelayMs, context.PluginLifetime).ConfigureAwait(false);
            var arguments = RestoreArguments(request).ToDictionary(pair => pair.Key, pair => pair.Value);
            arguments["status"] = IsSuccessful(outcome) ? "success" : "failed";
            await context.HostServices.KnotLink.SendAsync(
                "restore_finished",
                arguments,
                CancellationToken.None).ConfigureAwait(false);

            context.HostServices.Logger.Log(
                DiagnosticSeverity.Information,
                $"KnotLink restore_finished sent for '{Path.GetFileName(request.Folder.Path)}' with status '{arguments["status"]}'.");
        }
        catch (Exception ex)
        {
            context.HostServices.Logger.Log(
                DiagnosticSeverity.Warning,
                "Failed to report restore mutation completion through KnotLink.",
                ex);
        }
    }

    private async ValueTask ReportHotRestoreCompletedAsync(
        RestoreCoordinatorRequest request,
        OperationOutcome outcome,
        RejoinResult rejoinResult,
        PluginInvocationContext context)
    {
        try
        {
            var arguments = RestoreArguments(request).ToDictionary(pair => pair.Key, pair => pair.Value);
            arguments["status"] = ResolveHotRestoreStatus(outcome, rejoinResult);
            await context.HostServices.KnotLink.SendAsync(
                "hot_restore_complete",
                arguments,
                CancellationToken.None).ConfigureAwait(false);

            context.HostServices.Logger.Log(
                DiagnosticSeverity.Information,
                $"KnotLink hot_restore_complete sent with status '{arguments["status"]}'.");
        }
        catch (Exception ex)
        {
            context.HostServices.Logger.Log(
                DiagnosticSeverity.Warning,
                "Failed to report final hot-restore completion through KnotLink.",
                ex);
        }
    }

    private async ValueTask<RejoinResult> RequestWorldRejoinAsync(
        RestoreCoordinatorRequest request,
        PluginInvocationContext context)
    {
        // 模组收到 restore_finished 后需要从退出世界阶段切换到可重进状态；过早发送会被客户端丢弃。
        context.HostServices.Logger.Log(
            DiagnosticSeverity.Information,
            $"Waiting {PostRestoreStabilizeMs} ms before sending KnotLink rejoin_world.");
        await Task.Delay(PostRestoreStabilizeMs, context.PluginLifetime).ConfigureAwait(false);

        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _pendingRejoin, pending);
        try
        {
            context.HostServices.Logger.Log(
                DiagnosticSeverity.Information,
                $"Sending KnotLink rejoin_world for '{Path.GetFileName(request.Folder.Path)}'.");
            await context.HostServices.KnotLink.SendAsync(
                "rejoin_world",
                RestoreArguments(request),
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                return await pending.Task.WaitAsync(TimeSpan.FromMilliseconds(RejoinTimeoutMs)).ConfigureAwait(false)
                    ? RejoinResult.Succeeded
                    : RejoinResult.Failed;
            }
            catch (TimeoutException)
            {
                context.HostServices.Logger.Log(
                    DiagnosticSeverity.Warning,
                    "Timed out waiting for KnotLink REJOIN_RESULT.");
                return RejoinResult.TimedOut;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingRejoin, null, pending);
        }
    }

    private static PluginCommandResult Acknowledge(TaskCompletionSource<bool>? pending, string message)
    {
        if (pending is null || !pending.TrySetResult(true))
            return CommandFailure("minerewind.knotlink_ack_not_expected");
        return Success(message);
    }

    private static async ValueTask<(ConfigSnapshot Config, FolderSnapshot Folder)?> FindActiveWorldAsync(
        PluginInvocationContext context)
    {
        var config = await FindActiveConfigAsync(context).ConfigureAwait(false);
        var folder = FindActiveFolder(config);
        return config is null || folder is null ? null : (config, folder);
    }

    private static async ValueTask<PluginCommandResult> RequestCurrentBackupAsync(
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        var active = await FindActiveWorldAsync(context).ConfigureAwait(false);
        if (!active.HasValue) return CommandFailure("minerewind.command_active_world_not_found");
        var (config, folder) = active.Value;

        var options = new BackupRequestOptions
        {
            Comment = TryGetValue(arguments, "comment", out var comment) ? comment : string.Empty
        };

        // KnotLink responder 串行处理消息：必须先回复 BACKUP，后台备份才能继续接收 WORLD_SAVED。
        QueueHostOperation(
            context,
            "BACKUP current_save",
            cancellationToken => context.HostServices.Backups.RequestAsync(
                config.ConfigId,
                folder.FolderId,
                options,
                cancellationToken));
        return Success($"Backup started for '{folder.DisplayName}'.");
    }

    private static async ValueTask<PluginCommandResult> ListCurrentBackupsAsync(
        PluginInvocationContext context)
    {
        var active = await FindActiveWorldAsync(context).ConfigureAwait(false);
        if (!active.HasValue) return CommandFailure("minerewind.command_active_world_not_found");
        var (config, folder) = active.Value;
        var history = await context.HostServices.History.QueryAsync(
            config.ConfigId,
            folder.FolderId,
            context.OperationCancellation).ConfigureAwait(false);
        var data = string.Join(
            ';',
            history.OrderByDescending(item => item.CreatedAt)
                .Select(item => item.ArchiveFileName)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        return new PluginCommandResult(
            OperationOutcome.Success,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["data"] = JsonSerializer.SerializeToElement(data)
            },
            Array.Empty<PluginDiagnostic>());
    }

    private async ValueTask<PluginCommandResult> RequestCurrentRestoreAsync(
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        var active = await FindActiveWorldAsync(context).ConfigureAwait(false);
        if (!active.HasValue) return CommandFailure("minerewind.command_active_world_not_found");
        var (config, folder) = active.Value;
        var requestedFile = TryGetValue(arguments, "file", out var file) ? file : null;
        var forcePreserve = TryBoolean(arguments, "preserve_player_data");

        if (string.IsNullOrWhiteSpace(requestedFile))
        {
            if (forcePreserve) _preservePlayerDataQuickFolders[folder.FolderId] = 0;

            // 未指定归档时必须交由宿主按活动 Workspace 的唯一局部分支尖端解析，
            // 不能用展示时间倒序猜测恢复目标，否则其他分支的新提交会污染 Quick Restore。
            QueueHostOperation(
                context,
                "RESTORE current_save",
                cancellationToken => context.HostServices.Restores.RequestQuickAsync(
                    config.ConfigId,
                    folder.FolderId,
                    cancellationToken),
                () => _preservePlayerDataQuickFolders.TryRemove(folder.FolderId, out _));
            return Success($"Restore started for '{folder.DisplayName}'.");
        }

        var history = await context.HostServices.History.QueryAsync(
            config.ConfigId,
            folder.FolderId,
            context.OperationCancellation).ConfigureAwait(false);
        var item = history.FirstOrDefault(value =>
            string.Equals(value.ArchiveFileName, requestedFile, StringComparison.OrdinalIgnoreCase));
        if (item is null) return CommandFailure("minerewind.command_history_not_found");
        if (forcePreserve) _preservePlayerDataVersionIds[item.VersionId] = 0;

        // 热还原会等待 WORLD_SAVE_AND_EXIT_COMPLETE；不能在当前 responder 回调内同步等待。
        QueueHostOperation(
            context,
            "RESTORE current_save",
            cancellationToken => context.HostServices.Restores.RequestAsync(
                config.ConfigId,
                folder.FolderId,
                item.VersionId,
                cancellationToken),
            () => _preservePlayerDataVersionIds.TryRemove(item.VersionId, out _));
        return Success($"Restore started for '{folder.DisplayName}'.");
    }

    private static bool TryBoolean(IReadOnlyDictionary<string, string> values, string key)
        => TryGetValue(values, key, out var value)
           && value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "y" or "on";

    private static bool TryGetValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        foreach (var pair in values)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            value = pair.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsCompatibleModVersion(string currentVersion, string requiredVersion)
        => ParseVersion(currentVersion).CompareTo(ParseVersion(requiredVersion)) >= 0;

    private static (int Major, int Minor, int Patch) ParseVersion(string value)
    {
        var parts = value.Split('.');
        return (
            parts.Length > 0 && int.TryParse(parts[0], out var major) ? major : 0,
            parts.Length > 1 && int.TryParse(parts[1], out var minor) ? minor : 0,
            parts.Length > 2 && int.TryParse(parts[2], out var patch) ? patch : 0);
    }

    private static void QueueHostOperation(
        PluginInvocationContext context,
        string operationName,
        Func<CancellationToken, ValueTask<OperationOutcome>> request,
        Action? completed = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await request(context.PluginLifetime).ConfigureAwait(false);
                if (!IsSuccessful(outcome))
                {
                    context.HostServices.Logger.Log(
                        DiagnosticSeverity.Warning,
                        $"{operationName} completed with outcome {outcome}.");
                }
            }
            catch (OperationCanceledException) when (context.PluginLifetime.IsCancellationRequested)
            {
                context.HostServices.Logger.Log(
                    DiagnosticSeverity.Warning,
                    $"{operationName} was canceled because the plugin session is stopping.");
            }
            catch (Exception ex)
            {
                context.HostServices.Logger.Log(
                    DiagnosticSeverity.Error,
                    $"{operationName} failed after it was accepted.",
                    ex);
            }
            finally
            {
                completed?.Invoke();
            }
        }, CancellationToken.None);
    }

    private static PluginCommandResult Result(OperationOutcome outcome, string message)
        => new(
            outcome,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["message"] = JsonSerializer.SerializeToElement(message)
            },
            Array.Empty<PluginDiagnostic>());

    private static PluginCommandResult Success(string message)
        => Result(OperationOutcome.Success, message);

    private static string ResolveHotRestoreStatus(OperationOutcome outcome, RejoinResult rejoinResult)
    {
        if (!IsSuccessful(outcome))
        {
            return rejoinResult == RejoinResult.Succeeded
                ? "restore_failed_rejoined"
                : "restore_failed_rejoin_failed";
        }

        return rejoinResult switch
        {
            RejoinResult.Succeeded => "full_success",
            RejoinResult.TimedOut => "restore_ok_rejoin_timeout",
            _ => "restore_ok_rejoin_failed"
        };
    }

    private enum RejoinResult
    {
        Succeeded,
        Failed,
        TimedOut
    }
}
