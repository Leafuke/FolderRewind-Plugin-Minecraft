using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    private static readonly BackupScopeId SelectedRegionsScopeId = new(new OwnerId(PluginIdentity), "selected-regions");
    private static readonly JsonElement SelectedRegionsSchema = Json("""
        {
          "type": "object",
          "required": ["regions"],
          "properties": {
            "regions": {
              "type": "string",
              "description": "Semicolon-separated region coordinates, for example 0,0;-1,2"
            }
          },
          "additionalProperties": false
        }
        """);

    public IReadOnlyList<BackupScopeDescriptor> Scopes { get; } =
    [
        new(SelectedRegionsScopeId, "Selected Minecraft regions", SelectedRegionsSchema)
    ];

    IReadOnlyList<KnotLinkCommandDescriptor> IKnotLinkIntegrationCapability.Commands { get; } =
    [
        CurrentSaveCommand("BACKUP", "Back up the currently active Minecraft world"),
        CurrentSaveCommand("LIST_BACKUPS", "List backups for the currently active Minecraft world"),
        CurrentSaveCommand("RESTORE", "Restore the currently active Minecraft world"),
        new("HANDSHAKE_RESPONSE", "Report the companion mod version"),
        new("WORLD_SAVED", "Acknowledge that the active world was saved"),
        new("WORLD_SAVE_AND_EXIT_COMPLETE", "Acknowledge that save-and-exit completed"),
        new("REJOIN_RESULT", "Report the automatic world rejoin result")
    ];

    private static KnotLinkCommandDescriptor CurrentSaveCommand(string command, string description)
        => new(command, description)
        {
            RequiredArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["current_save"] = "true"
            }
        };

    public ValueTask<FilePolicyResult> ResolveAsync(
        FilePolicyRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        return ValueTask.FromResult(new FilePolicyResult(
            [
                "session.lock",
                "**/session.lock",
                "voxy/**",
                "**/DistantHorizons.sqlite",
                "**/DistantHorizons.sqlite-shm",
                "**/DistantHorizons.sqlite-wal"
            ],
            Array.Empty<string>(),
            Array.Empty<PluginDiagnostic>()));
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
        if (!TryString(request.Parameters, "regions", out var raw)
            && !TryString(request.Parameters, "selectedRegions", out raw))
        {
            return ValueTask.FromResult(new BackupScopeResult(
                OperationReadiness.Blocked,
                Array.Empty<string>(),
                [Diagnostic("minerewind.scope_regions_required", DiagnosticSeverity.Error, "BackupScope")]));
        }

        var regions = ParseRegions(raw);
        if (regions.Count == 0)
        {
            return ValueTask.FromResult(new BackupScopeResult(
                OperationReadiness.Blocked,
                Array.Empty<string>(),
                [Diagnostic("minerewind.scope_regions_invalid", DiagnosticSeverity.Error, "BackupScope")]));
        }
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "level.dat", "level.dat_old", "icon.png", "datapacks/**", "data/**", "playerdata/**", "advancements/**", "stats/**"
        };
        foreach (var (x, z) in regions)
        {
            foreach (var family in new[] { "region", "entities", "poi" })
            {
                patterns.Add($"{family}/r.{x}.{z}.mca");
                patterns.Add($"DIM-1/{family}/r.{x}.{z}.mca");
                patterns.Add($"DIM1/{family}/r.{x}.{z}.mca");
                patterns.Add($"dimensions/**/{family}/r.{x}.{z}.mca");
            }
        }
        return ValueTask.FromResult(new BackupScopeResult(
            OperationReadiness.Ready,
            patterns.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Array.Empty<PluginDiagnostic>()));
    }

    public ValueTask<FolderMetadataResult> ReadAsync(
        FolderMetadataRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        var path = ResolveWorldPath(request.Folder.Path);
        if (path is null)
        {
            return ValueTask.FromResult(new FolderMetadataResult(
                new Dictionary<string, string>(),
                [Diagnostic("minerewind.metadata_world_missing", DiagnosticSeverity.Warning, "FolderMetadata")]));
        }
        var details = NbtHelper.TryGetWorldDetails(path);
        var regionCount = Directory.EnumerateFiles(path, "r.*.*.mca", SearchOption.AllDirectories).Count();
        if (details is null)
        {
            return ValueTask.FromResult(new FolderMetadataResult(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldName"] = Path.GetFileName(path),
                    ["regionFileCount"] = regionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                [Diagnostic("minerewind.metadata_level_dat_invalid", DiagnosticSeverity.Warning, "FolderMetadata")]));
        }
        return ValueTask.FromResult(new FolderMetadataResult(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldName"] = string.IsNullOrWhiteSpace(details.LevelName) ? Path.GetFileName(path) : details.LevelName,
                ["gameMode"] = details.GameMode,
                ["seed"] = details.Seed,
                ["totalTime"] = details.TotalTime?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["dayTime"] = details.DayTime?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["lastPlayed"] = details.LastPlayed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["hasPlayerData"] = details.HasPlayerData.ToString(),
                ["dataVersion"] = details.DataVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["worldFormat"] = details.IsNewFormat ? "26.1+" : "legacy",
                ["regionFileCount"] = regionCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            Array.Empty<PluginDiagnostic>()));
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

    private static IReadOnlyList<(int X, int Z)> ParseRegions(string value)
    {
        var result = new HashSet<(int X, int Z)>();
        foreach (var token in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var coordinates = token.Split(',', StringSplitOptions.TrimEntries);
            if (coordinates.Length == 2
                && int.TryParse(coordinates[0], out var x)
                && int.TryParse(coordinates[1], out var z))
            {
                result.Add((x, z));
            }
        }
        return result.OrderBy(region => region.X).ThenBy(region => region.Z).ToArray();
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
