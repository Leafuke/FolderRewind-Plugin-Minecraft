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
        var worldPath = ResolveWorldPath(sourceRoot);
        var errorCode = string.Empty;
        if (worldPath is null
            || !MinecraftRegionBackupScope.TryBuild(sourceRoot, worldPath, parameters, out var patterns, out errorCode))
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
                Array.Empty<FolderMetadataField>(),
                [Diagnostic("minerewind.metadata_world_missing", DiagnosticSeverity.Warning, "FolderMetadata")]));
        }
        var details = NbtHelper.TryGetWorldDetails(path);
        var regionCount = Directory.EnumerateFiles(path, "r.*.*.mca", SearchOption.AllDirectories).Count();
        if (details is null)
        {
            return ValueTask.FromResult(new FolderMetadataResult(
                [
                    CreateMetadataField("worldName", "World name", "世界名称", Path.GetFileName(path)),
                    CreateMetadataField("regionFileCount", "Region files", "区域文件数", FormatNumber(regionCount))
                ],
                [Diagnostic("minerewind.metadata_level_dat_invalid", DiagnosticSeverity.Warning, "FolderMetadata")]));
        }
        return ValueTask.FromResult(new FolderMetadataResult(
            [
                CreateMetadataField(
                    "worldName",
                    "World name",
                    "世界名称",
                    string.IsNullOrWhiteSpace(details.LevelName) ? Path.GetFileName(path) : details.LevelName),
                CreateMetadataField("gameMode", "Game mode", "游戏模式", LocalizeGameMode(details.GameMode)),
                CreateMetadataField("seed", "Seed", "种子", details.Seed),
                CreateMetadataField("worldDays", "World days", "世界天数", FormatWorldDays(details.TotalTime)),
                CreateMetadataField("worldTotalTime", "World total time", "世界总时间", FormatWorldTicks(details.TotalTime)),
                CreateMetadataField("lastPlayed", "Last played", "最近游玩", FormatLastPlayed(details.LastPlayed)),
                CreateMetadataField(
                    "hasPlayerData",
                    "Player data",
                    "玩家数据",
                    details.HasPlayerData ? MetadataText("Yes", "是") : MetadataText("No", "否")),
                CreateMetadataField("dataVersion", "Data version", "数据版本", FormatNumber(details.DataVersion)),
                CreateMetadataField(
                    "worldFormat",
                    "Format",
                    "存档格式",
                    details.IsNewFormat
                        ? MetadataValue("Minecraft 26.1+")
                        : MetadataText("Legacy (< 26.1)", "旧版 (< 26.1)")),
                CreateMetadataField("regionFileCount", "Region files", "区域文件数", FormatNumber(regionCount))
            ],
            Array.Empty<PluginDiagnostic>()));
    }

    public async ValueTask<VersionMetadataCaptureResult> CaptureAsync(
        VersionMetadataCaptureRequest request,
        PluginInvocationContext context)
    {
        EnsureActivated();
        ValidateKind(request.Config.Kind);
        try
        {
            await using var stream = await request.Source.OpenReadAsync(
                "level.dat",
                context.OperationCancellation).ConfigureAwait(false);
            var file = new NbtFile();
            file.LoadFromStream(stream, NbtCompression.AutoDetect);
            var data = file.RootTag["Data"] as NbtCompound
                ?? throw new InvalidDataException("level.dat has no Data compound.");
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["dataVersion"] = (data["DataVersion"] as NbtInt)?.Value,
                ["levelName"] = (data["LevelName"] as NbtString)?.Value,
                ["gameType"] = (data["GameType"] as NbtInt)?.Value,
                ["lastPlayed"] = (data["LastPlayed"] as NbtLong)?.Value
            });
            return new VersionMetadataCaptureResult(
                [new CapturedVersionMetadata("minecraft.world", 1, payload)],
                []);
        }
        catch (OperationCanceledException) when (context.OperationCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VersionMetadataCaptureResult(
                [],
                [new PluginDiagnostic(
                    "minerewind.version_metadata_read_failed",
                    DiagnosticSeverity.Warning,
                    "VersionMetadata",
                    PluginIdentity,
                    new Dictionary<string, string> { ["message"] = ex.Message })]);
        }
    }

    private static FolderMetadataField CreateMetadataField(
        string key,
        string englishDisplayName,
        string chineseDisplayName,
        string value)
        => CreateMetadataField(
            key,
            englishDisplayName,
            chineseDisplayName,
            MetadataValue(value));

    private static FolderMetadataField CreateMetadataField(
        string key,
        string englishDisplayName,
        string chineseDisplayName,
        LocalizedText value)
        => new(key, MetadataText(englishDisplayName, chineseDisplayName), value);

    private static LocalizedText MetadataText(string english, string chinese)
        => new(
            english,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["en-US"] = english,
                ["zh-CN"] = chinese
            });

    private static LocalizedText MetadataValue(string value)
        => new(value ?? string.Empty, new Dictionary<string, string>());

    private static LocalizedText LocalizeGameMode(string gameMode)
        => gameMode switch
        {
            "Survival" => MetadataText("Survival", "生存模式"),
            "Creative" => MetadataText("Creative", "创造模式"),
            "Adventure" => MetadataText("Adventure", "冒险模式"),
            "Spectator" => MetadataText("Spectator", "旁观模式"),
            _ => MetadataValue(gameMode)
        };

    private static string FormatNumber(IFormattable? value)
        => value?.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FormatWorldTicks(long? totalTime)
    {
        if (totalTime is not long ticks) return string.Empty;
        try
        {
            return TimeSpan.FromSeconds(ticks / 20.0)
                .ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatWorldDays(long? totalTime)
        => totalTime is long ticks
            ? (ticks / 24000.0).ToString("F1", CultureInfo.InvariantCulture)
            : string.Empty;

    private static string FormatLastPlayed(long? lastPlayed)
    {
        if (lastPlayed is not long unixEpochMilliseconds) return string.Empty;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixEpochMilliseconds)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
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
