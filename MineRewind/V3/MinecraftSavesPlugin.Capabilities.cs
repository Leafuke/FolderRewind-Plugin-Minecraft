using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using FolderRewind.Plugin.Abstractions;
using fNbt;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private static readonly BackupScopeId SelectedRegionsScopeId = new(new OwnerId(PluginIdentity), "selected-regions");
    private static readonly JsonElement SelectedRegionsSchema = BuildSelectedRegionsSchema();

    public IReadOnlyList<BackupScopeDescriptor> Scopes { get; } =
    [
        new(
            SelectedRegionsScopeId,
            ScopeText("Selected Minecraft regions", "选定 Minecraft 区域"),
            SelectedRegionsSchema)
    ];

    private static JsonElement BuildSelectedRegionsSchema()
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["description"] = ScopeText(
                "Back up selected block-coordinate areas from explicitly selected Minecraft dimensions.",
                "仅备份所选 Minecraft 维度中的指定方块坐标区域。"),
            ["properties"] = new Dictionary<string, object?>
            {
                [MinecraftSpatialScope.Parameter] = new Dictionary<string, object?> { ["type"] = "string", ["x-folderrewind-hidden"] = true },
                ["dimension.overworld"] = Field("boolean", ScopeText("Overworld", "主世界"), defaultValue: true),
                ["dimension.nether"] = Field("boolean", ScopeText("Nether", "下界"), defaultValue: false),
                ["dimension.end"] = Field("boolean", ScopeText("The End", "末地"), defaultValue: false),
                ["areas"] = Field(
                    "string",
                    ScopeText("Block-coordinate areas", "方块坐标区域"),
                    ScopeText(
                        "One x1,z1,x2,z2 rectangle per line. Lines beginning with # are ignored.",
                        "每行填写一个 x1,z1,x2,z2 矩形；以 # 开头的行会被忽略。"),
                    format: "multiline")
            },
            ["additionalProperties"] = false
        });

    private static Dictionary<string, object?> Field(
        string type,
        string title,
        string? description = null,
        object? defaultValue = null,
        string? format = null)
    {
        var result = new Dictionary<string, object?> { ["type"] = type, ["title"] = title };
        if (description is not null) result["description"] = description;
        if (defaultValue is not null) result["default"] = defaultValue;
        if (format is not null) result["format"] = format;
        return result;
    }

    private static string ScopeText(string english, string chinese)
        => string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? chinese
            : english;

    IReadOnlyList<KnotLinkCommandDescriptor> IKnotLinkIntegrationCapability.Commands { get; } =
    [
        CurrentSaveCommand("BACKUP", "Back up the currently active Minecraft world"),
        CurrentSaveCommand("LIST_BACKUPS", "List backups for the currently active Minecraft world"),
        CurrentSaveCommand("LIST_VERSIONS", "List stable version IDs for the currently active Minecraft world"),
        CurrentSaveCommand("RESTORE", "Restore the currently active Minecraft world; default clean"),
        CurrentSaveCommand("AUTO_BACKUP", "Start periodic backup bound to the currently active world"),
        CurrentSaveCommand("STOP_AUTO_BACKUP", "Stop periodic backup for the currently active world"),
        CurrentSaveCommand("GET_IMPORTANCE", "Query a backup protection flag for the current world"),
        CurrentSaveCommand("MARK_IMPORTANT", "Mark a backup of the currently active world"),
        new("HANDSHAKE_RESPONSE", "Report the companion mod version during a pending handshake")
            { Arguments = [new("mod_version", "Required companion mod version; minimum 3.0.0.")] },
        new("WORLD_SAVED", "Acknowledge that the active world was saved; requires a pending save"),
        new("WORLD_SAVE_AND_EXIT_COMPLETE", "Acknowledge save-and-exit; requires a pending restore"),
        new("REJOIN_RESULT", "Report the automatic world rejoin result; requires a pending rejoin")
            { Arguments = [new("result", "failure means failed; omitted or another value means success.")] }
    ];

    IReadOnlyList<KnotLinkSignalDescriptor> IKnotLinkIntegrationCapability.Signals { get; } =
    [
        Signal("handshake", "Request companion mod handshake", "version", "action", "world", "min_mod_version"),
        Signal("handshake_ack", "Report handshake compatibility", "status", "mod_version"),
        Signal("pre_hot_backup", "Request world save before backup", "config", "folder_id", "world"),
        Signal("pre_hot_restore", "Request save-and-exit before restore", "config", "folder_id", "history_id", "world"),
        Signal("restore_cancelled", "Restore was canceled before mutation", "config", "folder_id", "history_id", "world", "reason"),
        Signal("restore_finished", "Host restore mutation finished", "config", "folder_id", "history_id", "world", "status"),
        Signal("rejoin_world", "Request automatic world rejoin", "config", "folder_id", "history_id", "world"),
        Signal("hot_restore_complete", "Final restore/rejoin result", "config", "folder_id", "history_id", "world", "status")
    ];

    private static KnotLinkSignalDescriptor Signal(string name, string description, params string[] fields)
        => new(name, description, fields.ToDictionary(field => field, field => $"Signal {field}.", StringComparer.Ordinal));

    private static KnotLinkCommandDescriptor CurrentSaveCommand(string command, string description)
        => new(command, description)
        {
            FunctionName = "minerewind_" + command.ToLowerInvariant() + "_current_save",
            IsTargetSelector = true,
            Returns = command == "GET_IMPORTANCE" ? ["file", "important"] : KnotLinkCoreCommands.Find(command)!.Returns,
            RequiredArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["current_save"] = "true" }
        };

    public async ValueTask<KnotLinkTargetResolution> ResolveTargetAsync(string command,
        IReadOnlyDictionary<string, string> arguments, PluginInvocationContext context)
    {
        EnsureActivated();
        if (!KnotLinkCoreCommands.FolderCommands.Contains(command)) return new(null, []);
        var configs = await context.HostServices.Configs.QueryAsync(MinecraftKind, context.OperationCancellation).ConfigureAwait(false);
        return ResolveActiveWorld(configs);
    }

    public ValueTask<FilePolicyResult> ResolveAsync(
        FilePolicyRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var observation = MinecraftWorldPathResolver.Observe(request.Folder.Path, context.OperationCancellation);
        var exclusions = observation.Worlds.SelectMany(world =>
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(request.Folder.Path), world.Path).Replace('\\', '/');
            var prefix = relative == "." ? "" : relative + "/**/";
            return MinecraftWorldFilePolicy.BackupExclusions.Select(rule => rule with { Pattern = prefix + rule.Pattern });
        }).Distinct().ToArray();
        return ValueTask.FromResult(new FilePolicyResult([], [], observation.Kind == World.WorldObservationKind.WorldEvidenceUnresolved
            ? [Diagnostic("minerewind.world_identity_unresolved", DiagnosticSeverity.Warning, "FilePolicy")] : [])
        { TypedRequiredExclusions = exclusions });
    }

    public ValueTask<BackupScopeResult> ResolveAsync(
        BackupScopeRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        if (request.ScopeId != SelectedRegionsScopeId)
        {
            return ValueTask.FromResult(new BackupScopeResult(
                OperationReadiness.Blocked,
                Array.Empty<string>(),
                [Diagnostic("minerewind.scope_unknown", DiagnosticSeverity.Error, "BackupScope")]));
        }
        var parameters = request.Parameters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ValueKind switch
            {
                JsonValueKind.String => pair.Value.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => pair.Value.ToString()
            },
            StringComparer.OrdinalIgnoreCase);
        var sourceRoot = Path.GetFullPath(request.Folder.Path);
        var observation = MinecraftWorldPathResolver.Observe(sourceRoot, context.OperationCancellation);
        if (observation.Kind == World.WorldObservationKind.WorldEvidenceUnresolved)
            return ValueTask.FromResult(new BackupScopeResult(OperationReadiness.Blocked, [],
                [Diagnostic("minerewind.scope_world_unresolved", DiagnosticSeverity.Error, "BackupScope")]));
        if (parameters.TryGetValue(MinecraftSpatialScope.Parameter, out var selectionJson) && !string.IsNullOrWhiteSpace(selectionJson))
        {
            var selection = JsonSerializer.Deserialize<SpatialScopeSelection[]>(selectionJson) ?? throw new InvalidDataException("Invalid spatial scope selection.");
            var result = MinecraftSpatialScope.Resolve(sourceRoot, selection, SelectedRegionsScopeId, context.OperationCancellation);
            return ValueTask.FromResult(new BackupScopeResult(OperationReadiness.Ready, result.IncludePatterns, result.Diagnostics));
        }
        if (observation.Worlds.Count > 1)
            return ValueTask.FromResult(new BackupScopeResult(OperationReadiness.Blocked, [],
                [Diagnostic("minerewind.scope_world_unresolved", DiagnosticSeverity.Error, "BackupScope")]));
        var worldPath = observation.Worlds.SingleOrDefault()?.Path;
        if (worldPath is null) return ValueTask.FromResult(new BackupScopeResult(OperationReadiness.Ready, ["**"], []));
        var errorCode = string.Empty;
        if (!MinecraftRegionBackupScope.TryBuild(sourceRoot, worldPath, parameters, out var patterns, out errorCode, context.OperationCancellation))
        {
            return ValueTask.FromResult(new BackupScopeResult(
                OperationReadiness.Blocked,
                Array.Empty<string>(),
                [Diagnostic(
                    worldPath is null ? "minerewind.scope_world_missing" : $"minerewind.scope_{errorCode}",
                    DiagnosticSeverity.Error,
                    "BackupScope")]));
        }
        return ValueTask.FromResult(new BackupScopeResult(
            OperationReadiness.Ready,
            patterns,
            Array.Empty<PluginDiagnostic>()));
    }

    public ValueTask<FolderMetadataResult> ReadAsync(FolderMetadataRequest request, PluginInvocationContext context)
    {
        EnsureActivated(); ValidateKind(request.Config.Kind);
        return MinecraftMetadataService.ReadAsync(request, context);
    }

    public ValueTask<IReadOnlyList<string>> GetRequiredSourcePathsAsync(
        ConfigSnapshot config, FolderSnapshot folder, PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(config.Kind);
        var world = World.WorldLocator.ResolveForDisplay(folder.Path, context.OperationCancellation, new World.WorldScanBudget());
        return ValueTask.FromResult<IReadOnlyList<string>>(world is null ? [] :
            [Path.GetRelativePath(Path.GetFullPath(folder.Path), Path.Combine(world, "level.dat")).Replace('\\', '/')]);
    }

    public ValueTask<VersionMetadataCaptureResult> CaptureAsync(VersionMetadataCaptureRequest request, PluginInvocationContext context)
    {
        EnsureActivated(); ValidateKind(request.Config.Kind);
        return MinecraftMetadataService.CaptureAsync(request, context);
    }

    public ValueTask<ConfigChangeProposal?> ProposeAsync(
        ConfigReconciliationRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var existing = request.Config.Folders
            .Select(folder => NormalizePath(folder.Path))
            .Where(path => path is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<ConfigChange>();
        foreach (var folder in request.Config.Folders)
        {
            var world = ResolveWorldPath(folder.Path);
            var saves = world is null ? null : Directory.GetParent(world)?.FullName;
            if (saves is null || !Directory.Exists(saves)) continue;
            IEnumerable<string> siblings;
            try { siblings = Directory.EnumerateDirectories(saves); }
            catch { continue; }
            foreach (var sibling in siblings.Where(IsWorld))
            {
                var normalized = NormalizePath(sibling);
                if (normalized is null || !existing.Add(normalized)) continue;
                additions.Add(new AddFolderChange(new FolderDraft(
                    sibling,
                    Path.GetFileName(sibling),
                    EmptyDraftStates)));
            }
        }
        if (additions.Count == 0) return ValueTask.FromResult<ConfigChangeProposal?>(null);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", additions.OfType<AddFolderChange>().Select(value => value.Folder.Path).Order()))))
            .ToLowerInvariant();
        return ValueTask.FromResult<ConfigChangeProposal?>(new ConfigChangeProposal(
            "minecraft-reconcile-" + fingerprint[..16],
            request.Config.ConfigId,
            request.Config.Revision,
            request.Reason,
            additions,
            Array.Empty<PluginDiagnostic>()));
    }

    public ValueTask<PluginCommandResult> ExecuteAsync(
        string command,
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        EnsureActivated();
        var known = ((IKnotLinkIntegrationCapability)this).Commands.Any(value =>
            string.Equals(value.Command, command, StringComparison.OrdinalIgnoreCase));
        if (!known)
            return ValueTask.FromResult(CommandFailure("minerewind.knotlink_command_unavailable"));
        return ExecuteInboundKnotLinkAsync(command, arguments, context);
    }

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

}
