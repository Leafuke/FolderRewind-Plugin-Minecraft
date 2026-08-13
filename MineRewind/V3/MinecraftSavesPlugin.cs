using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin :
    IFolderRewindPlugin,
    IDiscoveryCapability,
    IBackupConsistencyCapability,
    IFilePolicyCapability,
    IBackupScopeCapability,
    IFolderMetadataCapability,
    IConfigReconciliationCapability,
    IRestoreCoordinatorCapability,
    IPluginCommandCapability,
    IKnotLinkIntegrationCapability,
    IProviderStateMigrationCapability
{
    public const string PluginIdentity = "com.folderrewind.minerewind";
    public const string MinecraftKindIdentity = "minecraft-saves";
    public const string DiscoveryIdentity = PluginIdentity;
    public const string StateOwnerIdentity = PluginIdentity;

    private static readonly PluginId MineRewindPluginId = new(PluginIdentity);
    private static readonly OwnerId MineRewindOwnerId = new(PluginIdentity);
    private static readonly ConfigKindRef MinecraftKind = new(MineRewindOwnerId, MinecraftKindIdentity);
    private static readonly StateOwnerId MineRewindStateOwnerId = new(StateOwnerIdentity);
    private static readonly PluginCommandId HotBackupCommandId = new(MineRewindPluginId, "hotbackup.active-world");
    private static readonly PluginCommandId QuickRestoreCommandId = new(MineRewindPluginId, "hotrestore.active-world");
    private static readonly JsonElement HotBackupSchema = Json("""
        {
          "type": "object",
          "properties": {
            "configId": { "type": "string", "minLength": 1 },
            "folderId": { "type": "string", "format": "uuid" }
          },
          "additionalProperties": false
        }
        """);
    private static readonly JsonElement QuickRestoreSchema = Json("""
        {
          "type": "object",
          "properties": {
            "configId": { "type": "string", "minLength": 1 },
            "folderId": { "type": "string", "format": "uuid" },
            "historyItemId": { "type": "string", "minLength": 1 }
          },
          "additionalProperties": false
        }
        """);
    private static readonly IReadOnlyDictionary<StateOwnerId, ProviderStateDraft> EmptyDraftStates =
        new Dictionary<StateOwnerId, ProviderStateDraft>();

    private bool _activated;
    private bool _autoDiscoverSaves = true;
    private bool _preservePlayerData;

    public DiscoveryProviderId ProviderId { get; } = new(DiscoveryIdentity);
    public ConfigKindRef Kind => MinecraftKind;
    public StateOwnerId StateOwnerId => MineRewindStateOwnerId;
    public int CurrentSchemaVersion => 1;

    public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
    [
        new(HotBackupCommandId, "Back up the active Minecraft world", HotBackupSchema)
        {
            DefaultHotkey = "Alt+Ctrl+S",
            IsGlobalHotkey = true
        },
        new(QuickRestoreCommandId, "Restore the active Minecraft world to its latest backup", QuickRestoreSchema)
        {
            DefaultHotkey = "Alt+Ctrl+Z",
            IsGlobalHotkey = true
        }
    ];

    public ValueTask<PluginActivationResult> ActivateAsync(
        IPluginActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.PluginId != MineRewindPluginId || context.Settings.PluginId != MineRewindPluginId)
        {
            throw new InvalidOperationException("MineRewind activation identity does not match its manifest.");
        }
        if (_activated)
        {
            throw new InvalidOperationException("A MineRewind instance can be activated only once.");
        }

        _autoDiscoverSaves = ReadBoolean(context.Settings, "AutoDiscoverSaves", defaultValue: true);
        _preservePlayerData = ReadBoolean(context.Settings, "PreservePlayerData", defaultValue: false);
        context.RegisterCapability<IPluginCapability>(this);
        _activated = true;
        return ValueTask.FromResult(PluginActivationResult.Empty);
    }

    public ValueTask DeactivateAsync(CancellationToken cancellationToken)
    {
        _activated = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ArgumentNullException.ThrowIfNull(request);
        if (!_autoDiscoverSaves)
        {
            return ValueTask.FromResult(new DiscoveryResult(
                Array.Empty<DiscoveryCandidate>(),
                Array.Empty<PluginDiagnostic>()));
        }

        var diagnostics = new List<PluginDiagnostic>();
        var worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var userRoot in request.UserRoots ?? Array.Empty<string>())
        {
            context.OperationCancellation.ThrowIfCancellationRequested();
            foreach (var world in DiscoverWorlds(userRoot, diagnostics))
            {
                worlds.Add(world);
            }
        }

        var candidates = worlds
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(CreateCandidate)
            .ToArray();
        return ValueTask.FromResult(new DiscoveryResult(candidates, diagnostics));
    }

    public async ValueTask<IConsistencyLease> AcquireAsync(
        BackupConsistencyRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var sourcePath = ResolveWorldPath(request.Folder.Path)
            ?? throw new InvalidOperationException("Minecraft consistency requires a valid world folder.");
        var diagnostics = new List<PluginDiagnostic>();

        if (context.HostServices.KnotLink.IsAvailable)
        {
            await context.HostServices.KnotLink.SendAsync(
                "minebackup.save",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["configId"] = request.Config.ConfigId,
                    ["folderId"] = request.Folder.FolderId.ToString("D"),
                    ["world"] = Path.GetFileName(sourcePath)
                },
                context.OperationCancellation).ConfigureAwait(false);
        }
        else if (request.Intent == ConsistencyIntent.Require)
        {
            throw new InvalidOperationException("Required Minecraft consistency is unavailable because KnotLink is not connected.");
        }
        else
        {
            diagnostics.Add(Diagnostic(
                "minerewind.consistency_raw_source",
                DiagnosticSeverity.Warning,
                "BackupConsistency"));
        }

        string? snapshotPath = null;
        try
        {
            var temporaryRoot = await context.HostServices.TemporaryStorage.CreateDirectoryAsync(
                context.OperationCancellation).ConfigureAwait(false);
            snapshotPath = Path.Combine(temporaryRoot, "minerewind-snapshot-" + Guid.NewGuid().ToString("N"));
            CopyWorldSnapshot(sourcePath, snapshotPath, context.OperationCancellation);
            return new MinecraftConsistencyLease(snapshotPath, diagnostics, snapshotPath);
        }
        catch (Exception ex) when (request.Intent == ConsistencyIntent.Prefer)
        {
            if (snapshotPath is not null)
            {
                try { if (Directory.Exists(snapshotPath)) Directory.Delete(snapshotPath, recursive: true); } catch { }
            }
            diagnostics.Add(Diagnostic(
                "minerewind.snapshot_fallback",
                DiagnosticSeverity.Warning,
                "BackupConsistency",
                ("message", ex.Message)));
            return new MinecraftConsistencyLease(sourcePath, diagnostics, temporaryPath: null);
        }
        catch
        {
            if (snapshotPath is not null)
            {
                try { if (Directory.Exists(snapshotPath)) Directory.Delete(snapshotPath, recursive: true); } catch { }
            }
            throw;
        }
    }

    public async ValueTask<RestoreCoordinatorResult> CoordinateAsync(
        RestoreCoordinatorRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var diagnostics = new List<PluginDiagnostic>();
        var prepared = false;
        NbtHelper.PlayerDataSnapshot? preservedPlayerData = null;
        OperationOutcome outcome;
        try
        {
            if (!context.HostServices.KnotLink.IsAvailable)
            {
                return new RestoreCoordinatorResult(
                    OperationOutcome.Blocked,
                    [Diagnostic(
                        "minerewind.restore_knotlink_unavailable",
                        DiagnosticSeverity.Error,
                        "RestoreCoordinator")]);
            }

            await context.HostServices.KnotLink.SendAsync(
                "minebackup.save-and-exit",
                RestoreArguments(request),
                context.OperationCancellation).ConfigureAwait(false);
            prepared = true;

            if (_preservePlayerData)
            {
                preservedPlayerData = NbtHelper.ExtractPlayerData(request.Folder.Path);
            }

            var mutation = await request.ContinueMutationAsync(context.OperationCancellation).ConfigureAwait(false);
            outcome = mutation;
            if (IsSuccessful(mutation) && preservedPlayerData is not null)
            {
                if (!NbtHelper.ApplyPlayerData(request.Folder.Path, preservedPlayerData))
                {
                    diagnostics.Add(Diagnostic(
                        "minerewind.playerdata_restore_failed",
                        DiagnosticSeverity.Warning,
                        "RestoreCoordinator"));
                    if (outcome == OperationOutcome.Success)
                    {
                        outcome = OperationOutcome.SuccessWithWarnings;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (context.OperationCancellation.IsCancellationRequested)
        {
            outcome = OperationOutcome.Canceled;
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic(
                "minerewind.restore_coordination_failed",
                DiagnosticSeverity.Error,
                "RestoreCoordinator",
                ("message", ex.Message)));
            outcome = OperationOutcome.Failed;
        }

        if (prepared)
        {
            try
            {
                await context.HostServices.KnotLink.SendAsync(
                    "minebackup.rejoin",
                    RestoreArguments(request),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                diagnostics.Add(Diagnostic(
                    "minerewind.restore_rejoin_failed",
                    DiagnosticSeverity.Warning,
                    "RestoreCoordinator",
                    ("message", ex.Message)));
                if (outcome == OperationOutcome.Success)
                {
                    outcome = OperationOutcome.SuccessWithWarnings;
                }
            }
        }

        return new RestoreCoordinatorResult(outcome, diagnostics);
    }

    public async ValueTask<PluginCommandResult> ExecuteAsync(
        PluginCommandRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        if (request.Id.PluginId != MineRewindPluginId)
        {
            return CommandFailure("minerewind.command_owner_invalid");
        }
        TryString(request.Arguments, "configId", out var configId);
        var hasExplicitConfig = !string.IsNullOrWhiteSpace(configId);
        ConfigSnapshot? config = null;
        if (string.IsNullOrWhiteSpace(configId))
        {
            config = await FindActiveConfigAsync(context).ConfigureAwait(false);
            if (config is null) return CommandFailure("minerewind.command_active_world_not_found");
            configId = config.ConfigId;
        }

        if (request.Id == HotBackupCommandId)
        {
            Guid? folderId = TryGuid(request.Arguments, "folderId", out var backupFolderId)
                ? backupFolderId
                : hasExplicitConfig
                    ? null
                    : FindActiveFolder(config)?.FolderId;
            if (!hasExplicitConfig && !folderId.HasValue)
                return CommandFailure("minerewind.command_active_world_not_found");
            var outcome = await context.HostServices.Backups.RequestAsync(
                configId,
                folderId,
                context.OperationCancellation).ConfigureAwait(false);
            return new PluginCommandResult(
                outcome,
                new Dictionary<string, JsonElement>(),
                Array.Empty<PluginDiagnostic>());
        }

        if (request.Id == QuickRestoreCommandId)
        {
            var folderId = TryGuid(request.Arguments, "folderId", out var requestedFolderId)
                ? requestedFolderId
                : FindActiveFolder(config ??= await context.HostServices.Configs.FindAsync(
                    configId,
                    context.OperationCancellation).ConfigureAwait(false))?.FolderId;
            if (!folderId.HasValue)
            {
                return CommandFailure("minerewind.command_active_world_not_found");
            }
            TryString(request.Arguments, "historyItemId", out var historyItemId);
            if (string.IsNullOrWhiteSpace(historyItemId))
            {
                var history = await context.HostServices.History.QueryAsync(
                    configId,
                    folderId,
                    context.OperationCancellation).ConfigureAwait(false);
                historyItemId = history.OrderByDescending(value => value.CreatedAt).FirstOrDefault()?.HistoryItemId;
            }
            if (string.IsNullOrWhiteSpace(historyItemId)) return CommandFailure("minerewind.command_history_not_found");
            var outcome = await context.HostServices.Restores.RequestAsync(
                configId,
                folderId.Value,
                historyItemId,
                context.OperationCancellation).ConfigureAwait(false);
            return new PluginCommandResult(
                outcome,
                new Dictionary<string, JsonElement>(),
                Array.Empty<PluginDiagnostic>());
        }

        return CommandFailure("minerewind.command_unknown");
    }

    private static async ValueTask<ConfigSnapshot?> FindActiveConfigAsync(PluginInvocationContext context)
    {
        var configs = await context.HostServices.Configs.QueryAsync(MinecraftKind, context.OperationCancellation)
            .ConfigureAwait(false);
        return configs.FirstOrDefault(config => FindActiveFolder(config) is not null);
    }

    private static FolderSnapshot? FindActiveFolder(ConfigSnapshot? config)
        => config?.Folders.FirstOrDefault(folder => IsSessionLockHeld(folder.Path));

    private static bool IsSessionLockHeld(string folderPath)
    {
        var worldPath = ResolveWorldPath(folderPath);
        if (worldPath is null) return false;
        var lockPath = Path.Combine(worldPath, "session.lock");
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public ValueTask<ProviderStatePatch> MigrateAsync(
        ProviderStateSnapshot state,
        PluginInvocationContext context)
    {
        EnsureActivated();
        if (state.StateOwnerId != MineRewindStateOwnerId || state.SchemaVersion != 0)
        {
            throw new InvalidOperationException("MineRewind can migrate only its schema 0 provider state.");
        }

        return ValueTask.FromResult(new ProviderStatePatch(
            state.Location,
            MineRewindStateOwnerId,
            ExpectedSchemaVersion: 0,
            SchemaVersion: CurrentSchemaVersion,
            Data: state.Data.Clone()));
    }

    private static IEnumerable<string> DiscoverWorlds(
        string userRoot,
        ICollection<PluginDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(userRoot)) yield break;
        string root;
        try
        {
            root = Path.GetFullPath(userRoot);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic(
                "minerewind.discovery_root_invalid",
                DiagnosticSeverity.Warning,
                "Discovery",
                ("message", ex.Message)));
            yield break;
        }
        if (!Directory.Exists(root)) yield break;

        if (IsWorld(root))
        {
            yield return root;
            yield break;
        }

        foreach (var minecraftRoot in CandidateMinecraftRoots(root))
        {
            foreach (var savesRoot in CandidateSavesRoots(minecraftRoot))
            {
                if (!Directory.Exists(savesRoot)) continue;
                IEnumerable<string> directories;
                try { directories = Directory.EnumerateDirectories(savesRoot); }
                catch { continue; }
                foreach (var directory in directories)
                {
                    if (IsWorld(directory)) yield return Path.GetFullPath(directory);
                }
            }
        }
    }

    private static IEnumerable<string> CandidateMinecraftRoots(string root)
    {
        yield return root;
        var nested = Path.Combine(root, ".minecraft");
        if (!string.Equals(root, nested, StringComparison.OrdinalIgnoreCase) && Directory.Exists(nested))
        {
            yield return nested;
        }
    }

    private static IEnumerable<string> CandidateSavesRoots(string minecraftRoot)
    {
        if (string.Equals(Path.GetFileName(minecraftRoot), "saves", StringComparison.OrdinalIgnoreCase))
        {
            yield return minecraftRoot;
        }
        yield return Path.Combine(minecraftRoot, "saves");

        var versions = Path.Combine(minecraftRoot, "versions");
        if (!Directory.Exists(versions)) yield break;
        IEnumerable<string> versionDirectories;
        try { versionDirectories = Directory.EnumerateDirectories(versions); }
        catch { yield break; }
        foreach (var version in versionDirectories)
        {
            yield return Path.Combine(version, "saves");
        }
    }

    private static DiscoveryCandidate CreateCandidate(string worldPath)
    {
        var displayName = Path.GetFileName(worldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var folder = new FolderDraft(worldPath, displayName, EmptyDraftStates);
        var config = new ConfigDraft(
            MinecraftKind,
            displayName,
            [folder],
            EmptyDraftStates);
        return new DiscoveryCandidate(StableId(worldPath), displayName, [config]);
    }

    private static string StableId(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string? ResolveWorldPath(string path)
    {
        if (IsWorld(path)) return Path.GetFullPath(path);
        if (!Directory.Exists(path)) return null;
        try
        {
            var worlds = Directory.EnumerateDirectories(path).Where(IsWorld).Take(2).ToArray();
            return worlds.Length == 1 ? Path.GetFullPath(worlds[0]) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWorld(string path)
        => Directory.Exists(path) && File.Exists(Path.Combine(path, "level.dat"));

    private static bool ReadBoolean(PluginSettingsSnapshot settings, string key, bool defaultValue)
        => settings.Values.TryGetValue(key, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static IReadOnlyDictionary<string, string> RestoreArguments(RestoreCoordinatorRequest request)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["configId"] = request.Config.ConfigId,
            ["folderId"] = request.Folder.FolderId.ToString("D"),
            ["historyItemId"] = request.HistoryItemId,
            ["world"] = Path.GetFileName(request.Folder.Path)
        };

    private static bool IsSuccessful(OperationOutcome outcome)
        => outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings or OperationOutcome.NoChanges;

    private static bool TryString(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGuid(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string key,
        out Guid value)
    {
        value = Guid.Empty;
        return TryString(arguments, key, out var text)
               && Guid.TryParse(text, out value)
               && value != Guid.Empty;
    }

    private static PluginCommandResult CommandFailure(string code)
        => new(
            OperationOutcome.Blocked,
            new Dictionary<string, JsonElement>(),
            [Diagnostic(code, DiagnosticSeverity.Error, "PluginCommand")]);

    private static PluginDiagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        string capability,
        params (string Key, string Value)[] arguments)
        => new(
            code,
            severity,
            capability,
            PluginIdentity,
            arguments.ToDictionary(argument => argument.Key, argument => argument.Value, StringComparer.Ordinal));

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static void ValidateKind(ConfigKindRef kind)
    {
        if (kind != MinecraftKind)
        {
            throw new InvalidOperationException($"MineRewind cannot handle Config Kind '{kind}'.");
        }
    }

    private void EnsureActivated()
    {
        if (!_activated) throw new InvalidOperationException("MineRewind is not active.");
    }

    private static void CopyWorldSnapshot(string source, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(source, directory).Replace('\\', '/');
            if (IsDerivedCache(relative)) continue;
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (string.Equals(Path.GetFileName(relative), "session.lock", StringComparison.OrdinalIgnoreCase)
                || IsDerivedCache(relative)) continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsDerivedCache(string relativePath)
        => relativePath.StartsWith("voxy/", StringComparison.OrdinalIgnoreCase)
           || relativePath.Contains("/voxy/", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite-shm", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite-wal", StringComparison.OrdinalIgnoreCase);

    private sealed class MinecraftConsistencyLease(
        string sourcePath,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        string? temporaryPath) : IConsistencyLease
    {
        public string SourcePath { get; } = sourcePath;
        public IReadOnlyList<PluginDiagnostic> Diagnostics { get; } = diagnostics;
        public ValueTask DisposeAsync()
        {
            if (temporaryPath is not null)
            {
                try { if (Directory.Exists(temporaryPath)) Directory.Delete(temporaryPath, recursive: true); } catch { }
            }
            return ValueTask.CompletedTask;
        }
    }
}
