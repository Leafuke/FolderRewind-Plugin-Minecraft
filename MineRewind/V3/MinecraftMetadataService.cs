using System.Globalization;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;
using fNbt;

namespace MineRewind;

internal static class MinecraftMetadataService
{
    internal static ValueTask<FolderMetadataResult> ReadAsync(FolderMetadataRequest request, PluginInvocationContext context, WorldScanBudget? scanBudget = null)
    {
        var token = context.OperationCancellation;
        token.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(request.Folder.Path);
        var budget = scanBudget ?? new WorldScanBudget();
        var observed = WorldObserver.ObserveForDisplay(source, token, budget);
        if (observed.Kind == WorldObservationKind.Ordinary) return ValueTask.FromResult(new FolderMetadataResult([], []));
        var fields = new List<FolderMetadataField>();
        var diagnostics = new List<PluginDiagnostic>();
        var count = 0;
        var roots = observed.Worlds.Select(w => w.Path).ToArray();
        try
        {
            // One scan, including multi-world sources, rather than double-counting nested roots.
            foreach (var path in budget.Files(source, source, token, rejectLinks: false))
                if (roots.Any(root => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    && WorldPaths.TryRegionName(path, out _)) count++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add(Warning("scan", "The region count is incomplete.", "区域文件统计未完成。")); }
        var incomplete = budget.Incomplete || !observed.IsComplete || diagnostics.Count > 0;
        if (incomplete && diagnostics.Count == 0) diagnostics.Add(Warning("scan", "Some entries could not be counted.", "部分目录项未能纳入统计。"));
        if (roots.Length == 1)
        {
            var world = roots[0];
            fields.Add(Field("worldName", "World name", "世界名称", Path.GetFileName(world)));
            try
            {
                var data = WorldMetadataReader.Read(source, world, token,
                    _ => diagnostics.Add(Warning("field", "Some world metadata could not be read.", "部分世界信息无法读取。")));
                fields[0] = Field("worldName", "World name", "世界名称", data.Name);
                fields.Add(new("gameMode", Text("Game mode", "游戏模式"), data.GameType switch
                { 0 => Text("Survival", "生存模式"), 1 => Text("Creative", "创造模式"), 2 => Text("Adventure", "冒险模式"), 3 => Text("Spectator", "旁观模式"), _ => Text("Unknown", "未知") }));
                fields.Add(Field("seed", "Seed", "种子", data.Seed));
                fields.Add(Field("worldDays", "World days", "世界天数", data.Time is { } ticks ? (ticks / 24000d).ToString("F1", CultureInfo.InvariantCulture) : ""));
                fields.Add(Field("worldTotalTime", "World total time", "世界总时间", Ticks(data.Time)));
                fields.Add(Field("lastPlayed", "Last played", "最近游玩", LastPlayed(data.LastPlayed)));
                fields.Add(new("hasPlayerData", Text("Player data", "玩家数据"), data.HasPlayer ? Text("Yes", "是") : Text("No", "否")));
                fields.Add(Field("dataVersion", "Data version", "数据版本", data.DataVersion?.ToString(CultureInfo.InvariantCulture) ?? ""));
                fields.Add(new("worldFormat", Text("Format", "存档格式"), data.NewFormat ? Text("Minecraft 26.1+", "Minecraft 26.1+") : Text("Legacy (< 26.1)", "旧版 (< 26.1)")));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NbtFormatException or ArgumentException)
            { diagnostics.Add(Warning("level_dat_invalid", "World metadata is unreadable or exceeds its read budget.", "世界信息无法读取或超出读取限额。")); }
        }
        else fields.Add(Field("worldCount", "Worlds", "世界数量", roots.Length.ToString(CultureInfo.InvariantCulture)));
        fields.Add(new("regionFileCount", Text("Region files", "区域文件数"), incomplete
            ? Text($"At least {count} (count incomplete)", $"至少 {count}（统计未完成）") : Text(count.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture))));
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new FolderMetadataResult(fields.AsReadOnly(), diagnostics.DistinctBy(d => d.Code).ToArray()));
    }
    internal static async ValueTask<VersionMetadataCaptureResult> CaptureAsync(VersionMetadataCaptureRequest request, PluginInvocationContext context)
    {
        var token = context.OperationCancellation;
        var budget = new WorldScanBudget();
        var world = WorldLocator.ResolveForDisplay(request.Folder.Path, token, budget);
        if (world is null) return new([], budget.Incomplete ? [Warning("scan", "Metadata discovery is incomplete.", "信息发现未完成。")] : []);
        try
        {
            var relative = Path.GetRelativePath(request.Folder.Path, Path.Combine(world, "level.dat")).Replace('\\', '/');
            await using var input = await request.Source.OpenReadAsync(relative, token).ConfigureAwait(false);
            var root = BoundedNbtReader.Read(input, NbtReadLimits.Display, token,
                t => t.Name is null or "" or "Data" or "DataVersion" or "LevelName" or "GameType" or "LastPlayed").RootTag;
            var data = root["Data"] as NbtCompound ?? throw new InvalidDataException("Missing world Data compound.");
            return new([new CapturedVersionMetadata("minecraft.world", 1, JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            { ["dataVersion"] = (data["DataVersion"] as NbtInt)?.Value, ["levelName"] = (data["LevelName"] as NbtString)?.Value,
              ["gameType"] = (data["GameType"] as NbtInt)?.Value, ["lastPlayed"] = (data["LastPlayed"] as NbtLong)?.Value }))], []);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new([], [new("minerewind.version_metadata_read_failed", DiagnosticSeverity.Warning, "VersionMetadata",
            MinecraftSavesPlugin.PluginIdentity, Text("Version metadata could not be read from the captured input.", "无法从捕获输入读取版本信息。").Translations)]); }
    }
    private static LocalizedText Text(string en, string zh) => new(en, new Dictionary<string, string> { ["en-US"] = en, ["zh-CN"] = zh });
    private static FolderMetadataField Field(string key, string en, string zh, string value) => new(key, Text(en, zh), Text(value, value));
    private static PluginDiagnostic Warning(string suffix, string en, string zh) => new("minerewind.metadata_" + suffix,
        DiagnosticSeverity.Warning, "FolderMetadata", MinecraftSavesPlugin.PluginIdentity, Text(en, zh).Translations);
    private static string Ticks(long? ticks)
    { try { return ticks is { } t ? TimeSpan.FromSeconds(t / 20d).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture) : ""; } catch (OverflowException) { return ""; } }
    private static string LastPlayed(long? time)
    { try { return time is { } t ? DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : ""; } catch (ArgumentOutOfRangeException) { return ""; } }
}
