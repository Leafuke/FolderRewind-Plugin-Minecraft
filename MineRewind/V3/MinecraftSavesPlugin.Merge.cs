using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;
using MineRewind.World.Semantic;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : IMergeProviderCapability
{
    MergeProviderId IMergeProviderCapability.ProviderId => new(MineRewindPluginId, "minecraft-world");
    int IMergeProviderCapability.ContractVersion => 1;
    async ValueTask<MergeProviderResult> IMergeProviderCapability.AnalyzeAsync(MergeAnalysisRequest request, IMergePlanWriter plan, PluginInvocationContext context)
    {
        EnsureActivated();
        return await MinecraftMerge.AnalyzeAsync(request, plan, context.OperationCancellation).ConfigureAwait(false);
    }
    async ValueTask<MergeProviderResult> IMergeProviderCapability.BuildAsync(MergeBuildRequest request, IMergeOutputWorkspace output, PluginInvocationContext context)
    {
        EnsureActivated();
        return await MinecraftMerge.BuildAsync(request, output, context.OperationCancellation).ConfigureAwait(false);
    }
}

internal static class MinecraftMerge
{
    private sealed record RegionPlan(string Path, RegionAddress Address, MergeRegionIndex?[] Sides, bool Opaque, string[] Owned);
    private sealed record CoordinatePlan(string Key, string World, RegionPlan[] Regions);
    private sealed record GroupState(string IndexKey, bool WholeWorld, bool TerrainFirst)
    {
        public bool IsBlocked { get; init; }
    }
    private sealed record SubjectState(int Slot, int AutomaticSide);
    private sealed class WorldState
    {
        internal readonly List<string> Keys = [];
        internal bool EntitiesChanged, UnknownTerrain, UnsafeTerrainFirst, OursChanged, TheirsChanged, SidesDiffer;
    }
    private static readonly MergeDecisionKind[] SideChoices = [MergeDecisionKind.Ours, MergeDecisionKind.Theirs, MergeDecisionKind.Base, MergeDecisionKind.Delete];
    private static MergeProviderResult Success => new(OperationOutcome.Success, []);
    private static LocalizedText Text(string en, string zh) => new(en, new Dictionary<string, string> { ["en-US"] = en, ["zh-CN"] = zh });
    private static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static IMergeSourceView[] Views(MergeAnalysisRequest request) => [request.Base.Files, request.Ours.Files, request.Theirs.Files];
    private static void Validate(MergeAnalysisRequest request)
    {
        if (request.Kind.OwnerId.Value != MinecraftSavesPlugin.PluginIdentity || request.Kind.KindId != MinecraftSavesPlugin.MinecraftKindIdentity
            || !(request.PolicyId == "conservative" && request.PolicyVersion == "1" || request.PolicyId == "terrain-first" && request.PolicyVersion == "2"))
            throw new InvalidDataException("merge_unsupported_kind_or_policy");
    }

    internal static async Task<MergeProviderResult> AnalyzeAsync(MergeAnalysisRequest request, IMergePlanWriter plan, CancellationToken token)
    {
        Validate(request);
        var views = Views(request); var inventories = new Dictionary<string, MergeInputEntry>[3];
        for (var side = 0; side < 3; side++)
        {
            inventories[side] = new(StringComparer.Ordinal); string? cursor = null;
            do
            {
                var page = await views[side].ReadPageAsync(cursor, Math.Min(500, request.Limits.MaximumBatchSize), token).ConfigureAwait(false);
                foreach (var item in page.Entries.Where(e => !e.IsDirectory)) inventories[side].Add(item.RelativePath, item);
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        var paths = inventories.SelectMany(i => i.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var worlds = paths.Where(p => p == "level.dat" || p.EndsWith("/level.dat", StringComparison.Ordinal))
            .Select(p => p[..^9].TrimEnd('/')).OrderByDescending(p => p.Length).ToArray();
        var blockedWorlds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in worlds)
        {
            var world = root.Length == 0 ? "." : root;
            var issues = await MinecraftMergeCoverage.CheckAsync(request, world, token).ConfigureAwait(false);
            if (issues.Count == 0) continue;
            blockedWorlds.Add(world);
            await BlockAsync("coverage/" + Key(world), world, MinecraftMergeCoverage.Describe(issues),
                paths.Where(path => worlds.FirstOrDefault(w => w.Length == 0 || path.StartsWith(w + "/", StringComparison.Ordinal)) == root),
                JsonSerializer.SerializeToElement(issues)).ConfigureAwait(false);
        }
        var regions = new List<(string Path, RegionAddress Address)>();
        foreach (var path in paths.Where(p => p.EndsWith(".mca", StringComparison.OrdinalIgnoreCase)))
        {
            var root = worlds.FirstOrDefault(w => w.Length == 0 || path.StartsWith(w + "/", StringComparison.Ordinal));
            if (root is not null && blockedWorlds.Contains(root.Length == 0 ? "." : root)) continue;
            if (!MergeRegion.TryAddress(path, worlds, out var address) || !worlds.Contains(address!.WorldId == "." ? "" : address.WorldId, StringComparer.Ordinal))
            {
                await BlockAsync("unclassified/" + Key(path), path, Text("A readable world marker and supported region layout are required; this file will not fall back to file merge automatically.",
                    "缺少可读取的世界标识，或 region 路径布局无法识别；该文件不会自动退回文件合并。"), [path], JsonSerializer.SerializeToElement(path)).ConfigureAwait(false);
                continue;
            }
            regions.Add((path, address!));
        }
        if (regions.Select(r => r.Address.SortKey).Distinct(StringComparer.Ordinal).Count() != regions.Count)
            throw new InvalidDataException("merge_ambiguous_region_identity");
        var pathSets = inventories.Select(i => (IReadOnlySet<string>)i.Keys.ToHashSet(StringComparer.Ordinal)).ToArray();
        var states = new SortedDictionary<string, WorldState>(StringComparer.Ordinal);
        // One bounded coordinate index at a time; decoded world payloads never enter plan storage.
        foreach (var coordinate in regions.GroupBy(r => r.Address.GroupKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var world = coordinate.First().Address.WorldId;
            if (!states.TryGetValue(world, out var state)) states.Add(world, state = new());
            var entries = new List<RegionPlan>();
            foreach (var region in coordinate)
            {
                var sides = new MergeRegionIndex?[3]; var opaque = false;
                try
                {
                    for (var side = 0; side < 3; side++)
                        if (inventories[side].ContainsKey(region.Path))
                            sides[side] = await MergeRegion.ReadAsync(region.Path, region.Address, views[side].OpenReadAsync, pathSets[side], token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // Unchanged opaque data may pass through byte-for-byte. Its complete possible
                    // external family must also be identical; damaged changed data is never absence.
                    var family = paths.Where(p => IsExternalFamily(p, region.Path, region.Address)).Append(region.Path).ToArray();
                    if (family.Any(p => !SameFile(p, 0, 1) || !SameFile(p, 0, 2))) throw;
                    opaque = true;
                }
                var owned = opaque ? paths.Where(p => IsExternalFamily(p, region.Path, region.Address)).Append(region.Path).ToArray()
                    : sides.Where(s => s is not null).SelectMany(s => s!.Chunks).Select(c => c.ExternalPath).OfType<string>().Append(region.Path).Distinct(StringComparer.Ordinal).ToArray();
                entries.Add(new(region.Path, region.Address, sides, opaque, owned));
                if (opaque)
                {
                    state.UnknownTerrain |= region.Address.Component == ChunkComponentKind.Terrain;
                    state.UnsafeTerrainFirst |= region.Address.Component == ChunkComponentKind.Terrain;
                    continue;
                }
                var identities = sides.Select(Identity).ToArray();
                state.OursChanged |= identities[0] != identities[1]; state.TheirsChanged |= identities[0] != identities[2];
                state.SidesDiffer |= identities[1] != identities[2];
                if (region.Address.Component == ChunkComponentKind.Entities)
                    state.EntitiesChanged |= identities[0] != identities[1] || identities[0] != identities[2];
                if (region.Address.Component == ChunkComponentKind.Terrain)
                {
                    state.UnknownTerrain |= sides.Where(s => s is not null).SelectMany(s => s!.Chunks).Any(c => !c.SeparatedTerrain);
                    state.UnsafeTerrainFirst |= sides.Where(s => s is not null).SelectMany(s => s!.Chunks).Any(c => !c.SeparatedTerrain && !c.HasEmbeddedEntities);
                }
            }
            var key = "coordinates/" + Key(coordinate.Key); state.Keys.Add(key);
            await using var data = await plan.OpenDataWriteAsync(key, token).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(data, new CoordinatePlan(coordinate.Key, world, entries.ToArray()), cancellationToken: token).ConfigureAwait(false);
        }
        var terrainFirst = request.PolicyId == "terrain-first";
        var pinnedCount = 0; var pinnedExamples = new List<string>();
        foreach (var pair in states)
        {
            var world = pair.Key; var state = pair.Value;
            if (terrainFirst && state.UnsafeTerrainFirst) throw new InvalidDataException("merge_terrain_first_requires_separated_entity_layout: " + world);
            var whole = !terrainFirst && (state.EntitiesChanged || state.UnknownTerrain);
            var worldGroup = "world/" + Key(world);
            if (whole)
            {
                var manifestKey = "worlds/" + Key(world);
                await using (var data = await plan.OpenDataWriteAsync(manifestKey, token).ConfigureAwait(false))
                await using (var writer = new StreamWriter(data, new UTF8Encoding(false), 65536, leaveOpen: true))
                    foreach (var key in state.Keys) await writer.WriteLineAsync(key.AsMemory(), token).ConfigureAwait(false);
                await plan.AddGroupAsync(new(worldGroup, Text(world, world), JsonSerializer.SerializeToElement(new GroupState(manifestKey, true, false))), token).ConfigureAwait(false);
                var automatic = !state.SidesDiffer ? 1 : !state.OursChanged ? 2 : !state.TheirsChanged ? 1 : -1;
                await plan.AddSubjectsAsync([new("world", worldGroup, Key(world + request.Base.TreeDigest + request.Ours.TreeDigest + request.Theirs.TreeDigest),
                    Text($"World: {world}", $"世界：{world}"), Text("Entity movement or legacy layout requires one side for all region components across this world.",
                    "检测到实体变化或旧版布局，整个世界的所有维度及 Terrain、Entities、POI 必须统一采用一侧。"),
                    automatic >= 0 ? [] : SideChoices, automatic >= 0, JsonSerializer.SerializeToElement(new SubjectState(-1, automatic)))], token).ConfigureAwait(false);
            }
            foreach (var key in state.Keys)
            {
                await using var input = await plan.OpenDataReadAsync(key, token).ConfigureAwait(false);
                var coordinate = await JsonSerializer.DeserializeAsync<CoordinatePlan>(input, cancellationToken: token).ConfigureAwait(false) ?? throw new InvalidDataException();
                var group = whole ? worldGroup : "region/" + Key(coordinate.Key);
                var address = coordinate.Regions[0].Address;
                if (!whole) await plan.AddGroupAsync(new(group, Text($"{world} {address.DimensionId} ({address.X}, {address.Z})", $"{world} {address.DimensionId}（{address.X}, {address.Z}）"),
                    JsonSerializer.SerializeToElement(new GroupState(key, false, terrainFirst))), token).ConfigureAwait(false);
                foreach (var batch in coordinate.Regions.SelectMany(r => r.Owned).Distinct(StringComparer.Ordinal).Chunk(Math.Min(250, request.Limits.MaximumBatchSize / 2)))
                    await plan.ClaimPathsAsync(group, batch, batch, token).ConfigureAwait(false);
                if (whole) continue;
                var subjects = new List<MergeSubject>();
                var pinned = terrainFirst ? EmbeddedTerrainSlots(coordinate) : [];
                var components = coordinate.Regions.Where(r => !r.Opaque && (!terrainFirst || r.Address.Component != ChunkComponentKind.Entities)).ToArray();
                foreach (var slot in components.SelectMany(r => r.Sides.Where(s => s is not null).SelectMany(s => s!.Chunks)).Select(c => c.Slot).Distinct().Order())
                {
                    var ids = Enumerable.Range(0, 3).Select(side => string.Join("|", components.Select(r => Chunk(r.Sides[side], slot)?.PayloadHash ?? "absent"))).ToArray();
                    var keepOurs = pinned.Contains(slot);
                    if (!keepOurs && ids[0] == ids[1] && ids[0] == ids[2]) continue;
                    var automatic = keepOurs || ids[1] == ids[2] || ids[0] == ids[2] ? 1 : ids[0] == ids[1] ? 2 : -1;
                    var x = checked(address.X * 32 + slot % 32); var z = checked(address.Z * 32 + slot / 32);
                    if (keepOurs)
                    {
                        pinnedCount++;
                        if (pinnedExamples.Count < 8) pinnedExamples.Add($"{world} {address.DimensionId} ({x}, {z})");
                    }
                    subjects.Add(new(slot.ToString(CultureInfo.InvariantCulture), group, Key(string.Join("\0", ids)),
                        Text($"{world} {address.DimensionId} chunk ({x}, {z})" + (keepOurs ? " · kept from target" : ""),
                            $"{world} {address.DimensionId} 区块（{x}, {z}）" + (keepOurs ? " · 固定保留目标分支" : "")),
                        keepOurs ? Text("An input contains embedded entities. Terrain and POI at this coordinate are fixed to the target branch, including absence. Source changes here will not be merged; all ordinary entities also stay with the target branch.",
                            "输入中存在内嵌实体，此区块的地形与 POI 固定保留目标分支（目标中不存在时保持不存在）。来源分支对此区块的地形和 POI 修改不会合入；所有普通实体也保持目标分支。")
                        : Text(terrainFirst ? "Terrain and POI move together. Ordinary entities stay with Ours in every dimension." : "Terrain, Entities and POI move together at this coordinate.",
                            terrainFirst ? "Terrain 与 POI 统一选择；所有维度的普通实体保持 Ours。" : "此坐标的 Terrain、Entities 与 POI 统一选择。"),
                        automatic >= 0 ? [] : SideChoices, automatic >= 0, JsonSerializer.SerializeToElement(new SubjectState(slot, automatic))));
                }
                foreach (var batch in subjects.Chunk(Math.Min(500, request.Limits.MaximumBatchSize))) await plan.AddSubjectsAsync(batch, token).ConfigureAwait(false);
            }
        }
        if (pinnedCount == 0) return Success;
        var examples = string.Join("; ", pinnedExamples) + (pinnedCount > pinnedExamples.Count ? "; …" : "");
        var warning = Text($"Kept {pinnedCount} embedded-entity chunks and their POI from the target branch. Source terrain and POI changes at these coordinates were excluded: {examples}.",
            $"已将 {pinnedCount} 个含内嵌实体的区块及其 POI 固定为目标分支，来源分支在这些坐标的地形与 POI 修改不合入：{examples}。");
        return new(OperationOutcome.SuccessWithWarnings, [new("minecraft.merge.embedded_terrain_kept_ours", DiagnosticSeverity.Warning,
            "Merge", MinecraftSavesPlugin.PluginIdentity, new Dictionary<string, string>
            {
                ["message.default"] = warning.Default, ["message.en-US"] = warning.Translations["en-US"],
                ["message.zh-CN"] = warning.Translations["zh-CN"], ["count"] = pinnedCount.ToString(CultureInfo.InvariantCulture)
            })]);
        bool SameFile(string path, int a, int b) => inventories[a].GetValueOrDefault(path)?.Digest == inventories[b].GetValueOrDefault(path)?.Digest;
        async Task BlockAsync(string id, string label, LocalizedText description, IEnumerable<string> owned, JsonElement evidence)
        {
            await plan.AddGroupAsync(new(id, Text(label, label), JsonSerializer.SerializeToElement(new GroupState("", false, request.PolicyId == "terrain-first") { IsBlocked = true })), token).ConfigureAwait(false);
            foreach (var batch in owned.Chunk(Math.Min(500, request.Limits.MaximumBatchSize))) await plan.ClaimPathsAsync(id, batch, [], token).ConfigureAwait(false);
            await plan.AddSubjectsAsync([new("blocked", id, Key(evidence.GetRawText()), Text($"{label}: coverage unavailable", $"{label}：暂不能语义合并"),
                description, [], false, evidence) { IsBlocked = true }], token).ConfigureAwait(false);
        }
    }

    private static string Identity(MergeRegionIndex? region) => region is null ? "" : string.Join("|", region.Chunks.Select(c => c.Slot + ":" + c.PayloadHash));
    private static MergeChunk? Chunk(MergeRegionIndex? region, int slot) => region?.Chunks.FirstOrDefault(c => c.Slot == slot);
    private static HashSet<int> EmbeddedTerrainSlots(CoordinatePlan coordinate) => coordinate.Regions
        .Where(r => r.Address.Component == ChunkComponentKind.Terrain).SelectMany(r => r.Sides).OfType<MergeRegionIndex>()
        .SelectMany(r => r.Chunks).Where(c => c.HasEmbeddedEntities).Select(c => c.Slot).ToHashSet();
    private static bool IsExternalFamily(string path, string region, RegionAddress address)
    {
        if (path[..(path.LastIndexOf('/') + 1)] != region[..(region.LastIndexOf('/') + 1)]) return false;
        var name = path[(path.LastIndexOf('/') + 1)..].Split('.');
        return name.Length == 4 && name[0] == "c" && name[3] == "mcc"
            && int.TryParse(name[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(name[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z)
            && (int)Math.Floor(x / 32d) == address.X && (int)Math.Floor(z / 32d) == address.Z;
    }

    internal static async Task<MergeProviderResult> BuildAsync(MergeBuildRequest request, IMergeOutputWorkspace output, CancellationToken token)
    {
        Validate(request.Analysis);
        var state = request.Group.State.Deserialize<GroupState>() ?? throw new InvalidDataException();
        if (state.IsBlocked) throw new InvalidOperationException("merge_group_is_blocked");
        if (state.TerrainFirst != (request.Analysis.PolicyId == "terrain-first")) throw new InvalidDataException("merge_policy_changed");
        var choices = new Dictionary<int, int>(); string? cursor = null;
        do
        {
            var page = await request.Decisions.ReadPageAsync(cursor, 500, token).ConfigureAwait(false);
            foreach (var decision in page.Decisions)
            {
                var subject = decision.Subject.State.Deserialize<SubjectState>() ?? throw new InvalidDataException();
                var side = decision.Choice switch
                {
                    MergeDecisionKind.Automatic when decision.Subject.IsAutomatic => subject.AutomaticSide,
                    MergeDecisionKind.Base => 0, MergeDecisionKind.Ours => 1, MergeDecisionKind.Theirs => 2, MergeDecisionKind.Delete => -1,
                    _ => throw new InvalidDataException("merge_invalid_decision")
                };
                if (side is < -1 or > 2 || choices.Count > 1024) throw new InvalidDataException("merge_invalid_decision_count");
                choices.Add(subject.Slot, side);
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (state.WholeWorld && (choices.Count != 1 || !choices.ContainsKey(-1))) throw new InvalidDataException("merge_missing_world_decision");
        var views = Views(request.Analysis);
        var verifiedWorlds = new HashSet<string>(StringComparer.Ordinal);
        if (state.WholeWorld)
        {
            await using var manifest = await request.Decisions.OpenDataReadAsync(state.IndexKey, token).ConfigureAwait(false);
            using var reader = new StreamReader(manifest);
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } key) await Compose(key).ConfigureAwait(false);
        }
        else await Compose(state.IndexKey).ConfigureAwait(false);
        return Success;

        async Task Compose(string key)
        {
            await using var data = await request.Decisions.OpenDataReadAsync(key, token).ConfigureAwait(false);
            var coordinate = await JsonSerializer.DeserializeAsync<CoordinatePlan>(data, cancellationToken: token).ConfigureAwait(false) ?? throw new InvalidDataException();
            if (verifiedWorlds.Add(coordinate.World) && (await MinecraftMergeCoverage.CheckAsync(request.Analysis, coordinate.World, token).ConfigureAwait(false)).Count != 0)
                throw new InvalidOperationException("merge_coverage_changed_reanalyze");
            if (state.TerrainFirst && EmbeddedTerrainSlots(coordinate).Any(slot => !choices.TryGetValue(slot, out var side) || side != 1))
                throw new InvalidDataException("merge_embedded_terrain_requires_ours");
            foreach (var region in coordinate.Regions)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                var fixedSide = state.WholeWorld ? choices[-1] : region.Opaque || state.TerrainFirst && region.Address.Component == ChunkComponentKind.Entities ? 1 : (int?)null;
                var selected = new List<MergeChunkSource>();
                if (!region.Opaque)
                    for (var slot = 0; slot < 1024; slot++)
                    {
                        var side = fixedSide ?? choices.GetValueOrDefault(slot, 1);
                        if (side >= 0 && region.Sides[side] is { } source && Chunk(source, slot) is { } chunk) selected.Add(new(source, chunk, side));
                    }
                var copy = fixedSide;
                if (copy is null && selected.Count != 0)
                    for (var side = 0; side < 3; side++)
                        if (region.Sides[side] is { } source && source.Chunks.Count == selected.Count
                            && selected.All(s => s.Side == side)) { copy = side; break; }
                if (region.Opaque && fixedSide >= 0)
                {
                    foreach (var path in region.Owned) { await output.ReuseAsync(path, MergeInputSide.Ours, path, token).ConfigureAwait(false); used.Add(path); }
                }
                else if (!region.Opaque && copy is >= 0 && region.Sides[copy.Value] is { } reused)
                {
                    await output.ReuseAsync(region.Path, (MergeInputSide)copy.Value, region.Path, token).ConfigureAwait(false); used.Add(region.Path);
                    foreach (var path in reused.Chunks.Select(c => c.ExternalPath).OfType<string>())
                    { await output.ReuseAsync(path, (MergeInputSide)copy.Value, path, token).ConfigureAwait(false); used.Add(path); }
                }
                else if (selected.Count != 0)
                {
                    await using (var file = await output.OpenWriteAsync(region.Path, token).ConfigureAwait(false))
                        await MergeRegion.WriteAsync(selected, file, (side, path, ct) => views[side].OpenReadAsync(path, ct), token).ConfigureAwait(false);
                    used.Add(region.Path);
                    foreach (var item in selected.Where(s => s.Chunk.ExternalPath is not null))
                    {
                        var path = item.Chunk.ExternalPath!;
                        await output.ReuseAsync(path, (MergeInputSide)item.Side, path, token).ConfigureAwait(false); used.Add(path);
                    }
                    var external = selected.Where(s => s.Chunk.ExternalPath is not null).ToDictionary(s => s.Chunk.ExternalPath!, s => s.Side, StringComparer.Ordinal);
                    var verified = await MergeRegion.ReadAsync(region.Path, region.Address,
                        (path, ct) => path == region.Path ? output.OpenReadAsync(path, ct) : views[external[path]].OpenReadAsync(path, ct), used, token).ConfigureAwait(false);
                    if (verified.Chunks.Count != selected.Count || selected.Any(s => Chunk(verified, s.Chunk.Slot) is not { } c
                        || c.PayloadHash != s.Chunk.PayloadHash || c.Timestamp != s.Chunk.Timestamp || c.Compression != s.Chunk.Compression || c.ExternalPath != s.Chunk.ExternalPath))
                        throw new InvalidDataException("merge_output_verification_failed");
                }
                foreach (var path in region.Owned.Except(used, StringComparer.Ordinal)) await output.DeleteAsync(path, token).ConfigureAwait(false);
            }
        }
    }
}
