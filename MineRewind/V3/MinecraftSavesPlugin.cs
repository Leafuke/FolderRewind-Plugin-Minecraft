using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.Discovery;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin :
    IFolderRewindPlugin,
    IFixedSpatialPreviewCapability,
    IDiscoveryCapability,
    IDiscoveryDefinitionCatalog,
    IBackupConsistencyCapability,
    IFilePolicyCapability,
    IBackupScopeCapability,
    IFolderMetadataCapability,
    IVersionMetadataProviderCapability,
    IConfigReconciliationCapability,
    IRestoreCoordinatorCapability,
    IRestoreStagingPreparationCapability,
    IPluginCommandCapability,
    IKnotLinkIntegrationCapability,
    IKnotLinkTargetResolver,
    IProviderStateMigrationCapability
{
    public const string PluginIdentity = "com.folderrewind.minerewind";
    public const string MinecraftKindIdentity = "minecraft-saves";
    public const string MinecraftDefinitionIdentity = "minecraft-java";
    public const string BedrockKindIdentity = "minecraft-bedrock-saves";
    public const string BedrockDefinitionIdentity = "minecraft-bedrock";
    public const string DiscoveryIdentity = PluginIdentity;
    public const string StateOwnerIdentity = PluginIdentity;

    private static readonly PluginId MineRewindPluginId = new(PluginIdentity);
    private static readonly OwnerId MineRewindOwnerId = new(PluginIdentity);
    private static readonly ConfigKindRef MinecraftKind = new(MineRewindOwnerId, MinecraftKindIdentity);
    private static readonly ConfigKindRef BedrockKind = new(MineRewindOwnerId, BedrockKindIdentity);
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
    private static readonly IReadOnlyList<DiscoveryDefinitionDescriptor> DiscoveryDefinitions =
    [
        new(
            MinecraftDefinitionIdentity,
            "Minecraft: Java Edition",
            ["Minecraft", "Minecraft Java", "Minecraft: Java Edition"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
        new(
            BedrockDefinitionIdentity,
            "Minecraft: Bedrock Edition",
            ["Minecraft Bedrock", "Minecraft for Windows", "Minecraft 基岩版"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
    ];

    private bool _activated;
    private bool _autoDiscoverSaves = true;
    private bool _preservePlayerData;

    public DiscoveryProviderId ProviderId { get; } = new(DiscoveryIdentity);
    public IReadOnlyList<DiscoveryDefinitionDescriptor> Definitions => DiscoveryDefinitions;
    public ConfigKindRef Kind => MinecraftKind;
    public bool SupportsBoundedSourceView => true;
    public StateOwnerId StateOwnerId => MineRewindStateOwnerId;
    public int CurrentSchemaVersion => 1;
    public bool SupportsPlayerDataOverride => true;

    public IReadOnlyList<PluginCommandDescriptor> Commands { get; } =
    [
        new(HotBackupCommandId, "Back up the active Minecraft Java world", HotBackupSchema)
        {
            DefaultHotkey = "Alt+Ctrl+S",
            IsGlobalHotkey = true
        },
        new(QuickRestoreCommandId, "Restore the active Minecraft Java world to its latest backup", QuickRestoreSchema)
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
        _preview.Clear();
        ClearSpatialComparisons();
        _activated = false;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<DiscoveryResult> DiscoverAsync(
        DiscoveryRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ArgumentNullException.ThrowIfNull(request);
        if (!_autoDiscoverSaves)
        {
            return new DiscoveryResult(
                Array.Empty<DiscoveryCandidate>(),
                Array.Empty<PluginDiagnostic>());
        }

        var scan = await Task.Run(() => MinecraftDiscoveryService.Discover(request, context.OperationCancellation),
            context.OperationCancellation).ConfigureAwait(false);
        return new DiscoveryResult(scan.Instances.Select(CreateCandidate).ToArray(), scan.Diagnostics);
    }

    public string? ResolveDefinitionId(DiscoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ConfigDrafts.Any(draft => draft.Kind == BedrockKind)) return BedrockDefinitionIdentity;
        return candidate.ConfigDrafts.Any(draft => draft.Kind == MinecraftKind) ? MinecraftDefinitionIdentity : null;
    }

    public async ValueTask<IConsistencyLease> AcquireAsync(
        BackupConsistencyRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var sourceRoot = Path.GetFullPath(request.Folder.Path);
        var worldPath = ResolveWorldPath(sourceRoot);
        // Ordinary folders in a Minecraft configuration do not require world coordination.
        if (worldPath is null) return new MinecraftConsistencyLease(sourceRoot, [], null);
        var diagnostics = new List<PluginDiagnostic>();
        var worldState = ProbeWorldLock(worldPath);
        var worldIsActive = worldState.State == WorldLockState.InUse;
        if (worldState.State == WorldLockState.Unknown)
        {
            var diagnostic = LockProbeDiagnostic(worldState,
                request.Intent == ConsistencyIntent.Require ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "BackupConsistency");
            if (request.Intent == ConsistencyIntent.Require)
                throw new IOException($"{diagnostic.Code}: {worldState.LockPath}: {worldState.Message}", worldState.Error);
            diagnostics.Add(diagnostic);
        }

        if (worldIsActive && context.HostServices.KnotLink.IsAvailable)
        {
            try
            {
                var compatible = await PerformModHandshakeAsync(
                    "backup",
                    Path.GetFileName(worldPath),
                    context).ConfigureAwait(false);
                if (!compatible)
                {
                    if (request.Intent == ConsistencyIntent.Require)
                    {
                        throw new InvalidOperationException("A compatible Minecraft companion mod did not answer the KnotLink handshake.");
                    }
                    diagnostics.Add(Diagnostic(
                        "minerewind.consistency_handshake_unavailable",
                        DiagnosticSeverity.Warning,
                        "BackupConsistency"));
                }
                else
                {
                    var saved = await RequestWorldSaveForBackupAsync(request, worldPath, context)
                        .ConfigureAwait(false);
                    if (!saved)
                    {
                        if (request.Intent == ConsistencyIntent.Require)
                        {
                            throw new TimeoutException("Minecraft did not acknowledge WORLD_SAVED before the consistency timeout.");
                        }
                        diagnostics.Add(Diagnostic(
                            "minerewind.consistency_save_timeout",
                            DiagnosticSeverity.Warning,
                            "BackupConsistency"));
                    }
                }
            }
            catch (OperationCanceledException) when (context.OperationCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && request.Intent == ConsistencyIntent.Prefer)
            {
                diagnostics.Add(Diagnostic(
                    "minerewind.consistency_coordination_failed",
                    DiagnosticSeverity.Warning,
                    "BackupConsistency",
                    ("message", ex.Message)));
            }
        }
        else if (worldIsActive && request.Intent == ConsistencyIntent.Require)
        {
            throw new InvalidOperationException("Required Minecraft consistency is unavailable because KnotLink is not connected.");
        }
        else if (worldIsActive)
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
            var expectedSpace = request.SourceViewLimits is { } limits
                ? MinecraftSnapshotCopier.InspectSpace(sourceRoot, limits, context.OperationCancellation) : (long?)null;
            using var reservation = expectedSpace is { } bytes
                ? await context.HostServices.TemporaryStorage.ReserveAsync(temporaryRoot, bytes, context.OperationCancellation) : null;
            var copy = MinecraftSnapshotCopier.Copy(sourceRoot, snapshotPath, context.OperationCancellation, request.SourceViewLimits, expectedSpace);
            if (copy.SkippedLinkCount > 0)
            {
                diagnostics.Add(Diagnostic("minerewind.snapshot_links_skipped", DiagnosticSeverity.Warning,
                    "BackupConsistency",
                    ("count", copy.SkippedLinkCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("paths", JsonSerializer.Serialize(copy.SkippedLinkPaths))));
            }
            return new MinecraftConsistencyLease(snapshotPath, diagnostics, snapshotPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && request.Intent == ConsistencyIntent.Prefer)
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
            return new MinecraftConsistencyLease(sourceRoot, diagnostics, temporaryPath: null);
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

    public async ValueTask<RestoreStagingPreparationResult> PrepareAsync(
        RestoreStagingPreparationRequest request, PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var folder = request.Config.Folders.FirstOrDefault(value => value.FolderId == request.FolderId);
        if (folder is null || ResolveWorldPath(folder.Path) is null) return new([], []);
        if (!(request.PreservePlayerDataOverride ?? (_preservePlayerData || request.PreservePlayerData))) return new([], []);
        try
        {
            return new(await NbtHelper.PreparePlayerDataAsync(request.Current, request.Target,
                context.OperationCancellation).ConfigureAwait(false), []);
        }
        catch (OperationCanceledException) when (context.OperationCancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new([], [Diagnostic("minerewind.playerdata_restore_failed", DiagnosticSeverity.Error,
                "RestoreStagingPreparation", ("message", ex.Message))]);
        }
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
        if (hasExplicitConfig)
        {
            config = await context.HostServices.Configs.FindAsync(configId!, context.OperationCancellation).ConfigureAwait(false);
            if (config is null || config.Kind != MinecraftKind)
                return CommandFailure("minerewind.command_kind_unsupported");
        }
        Guid? activeFolderId = null;
        if (string.IsNullOrWhiteSpace(configId))
        {
            var configs = await context.HostServices.Configs.QueryAsync(MinecraftKind, context.OperationCancellation).ConfigureAwait(false);
            var selection = ResolveActiveWorld(configs);
            if (selection.Target is null) return new(OperationOutcome.Blocked, new Dictionary<string, JsonElement>(), selection.Diagnostics);
            config = configs.Single(item => item.ConfigId == selection.Target.ConfigId);
            activeFolderId = selection.Target.FolderId;
            configId = config.ConfigId;
        }

        if (request.Id == HotBackupCommandId)
        {
            Guid? folderId = TryGuid(request.Arguments, "folderId", out var backupFolderId)
                ? backupFolderId
                : hasExplicitConfig
                    ? null
                    : activeFolderId;
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
                : activeFolderId;
            if (!folderId.HasValue && config is not null)
            {
                var selection = ResolveActiveWorld([config]);
                if (selection.Target is null) return new(OperationOutcome.Blocked, new Dictionary<string, JsonElement>(), selection.Diagnostics);
                folderId = selection.Target.FolderId;
            }
            if (!folderId.HasValue)
            {
                return CommandFailure("minerewind.command_active_world_not_found");
            }
            TryString(request.Arguments, "historyItemId", out var historyItemId);
            if (string.IsNullOrWhiteSpace(historyItemId))
            {
                var quickOutcome = await context.HostServices.Restores.RequestQuickAsync(
                    configId,
                    folderId.Value,
                    context.OperationCancellation).ConfigureAwait(false);
                return new PluginCommandResult(
                    quickOutcome,
                    new Dictionary<string, JsonElement>(),
                    Array.Empty<PluginDiagnostic>());
            }
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

    private static DiscoveryCandidate CreateCandidate(MinecraftDiscoveredInstance instance)
    {
        var folders = instance.Worlds.OrderBy(world => world.Path, StringComparer.OrdinalIgnoreCase)
            .Select(world => new FolderDraft(world.Path, world.Name, EmptyDraftStates)).ToList();
        if (!string.IsNullOrWhiteSpace(instance.ModsPath)
            && !folders.Any(folder => string.Equals(folder.Path, instance.ModsPath, StringComparison.OrdinalIgnoreCase)))
            folders.Add(new FolderDraft(instance.ModsPath, "mods", EmptyDraftStates));
        var kind = instance.Edition == MinecraftEdition.Bedrock ? BedrockKind : MinecraftKind;
        var config = new ConfigDraft(kind, instance.Name, folders, EmptyDraftStates);
        var identity = instance.Edition == MinecraftEdition.Java
            ? StableId(instance.Path) : "bedrock-" + StableId(instance.Path);
        return new DiscoveryCandidate(identity, instance.Name, [config]);
    }
    private static string StableId(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string? ResolveWorldPath(string path)
        => MinecraftWorldPathResolver.TryResolveWorldPath(path);

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
            // 保留 1.8.x 模组使用的蛇形协议键；额外身份只用于 v3 诊断与关联。
            ["config"] = request.Config.ConfigId,
            ["folder_id"] = request.Folders.Single().FolderId.ToString("D"),
            ["history_id"] = request.TargetIdentity,
            ["world"] = Path.GetFileName(request.Folders.Single().Path)
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

    private sealed class MinecraftConsistencyLease(
        string sourcePath,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        string? temporaryPath) : IConsistencyLease
    {
        public string SourcePath { get; } = sourcePath;
        public bool IsStableSourceView => temporaryPath is not null;
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
