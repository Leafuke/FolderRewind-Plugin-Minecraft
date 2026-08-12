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
        new("minebackup.save", "Flush the active world to disk"),
        new("minebackup.save-and-exit", "Save and leave the active world before restore"),
        new("minebackup.rejoin", "Rejoin the world after restore")
    ];

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
        var level = new FileInfo(Path.Combine(path, "level.dat"));
        var regionCount = Directory.EnumerateFiles(path, "r.*.*.mca", SearchOption.AllDirectories).Count();
        return ValueTask.FromResult(new FolderMetadataResult(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldName"] = Path.GetFileName(path),
                ["levelDatLastWriteUtc"] = level.LastWriteTimeUtc.ToString("O"),
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
            string.Equals(value.Command, command, StringComparison.Ordinal));
        if (!known || !context.HostServices.KnotLink.IsAvailable)
            return ValueTask.FromResult(CommandFailure("minerewind.knotlink_command_unavailable"));
        return ExecuteKnotLinkAsync(command, arguments, context);
    }

    private static async ValueTask<PluginCommandResult> ExecuteKnotLinkAsync(
        string command,
        IReadOnlyDictionary<string, string> arguments,
        PluginInvocationContext context)
    {
        await context.HostServices.KnotLink.SendAsync(command, arguments, context.OperationCancellation)
            .ConfigureAwait(false);
        return new PluginCommandResult(
            OperationOutcome.Success,
            new Dictionary<string, JsonElement>(),
            Array.Empty<PluginDiagnostic>());
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

    private static string? PreservePlayerData(string worldPath)
    {
        var source = Path.Combine(worldPath, "playerdata");
        if (!Directory.Exists(source)) return null;
        var target = Path.Combine(Path.GetTempPath(), "MineRewind-playerdata-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source, target);
        return target;
    }

    private static void RestorePlayerData(string preserved, string worldPath)
    {
        var target = Path.Combine(worldPath, "playerdata");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        CopyDirectory(preserved, target);
    }

    private static void DeleteTemporaryPlayerData(
        string? path,
        ICollection<PluginDiagnostic> diagnostics)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic(
                "minerewind.playerdata_cleanup_failed",
                DiagnosticSeverity.Warning,
                "RestoreCoordinator",
                ("message", ex.Message)));
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
