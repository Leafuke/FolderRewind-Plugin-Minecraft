using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : ISpatialScopeSelectionCapability
{
    ConfigKindRef ISpatialScopeSelectionCapability.Kind => MinecraftKind;
    ValueTask<SpatialScopeResult> ISpatialScopeSelectionCapability.ResolveSelectionAsync(SpatialScopeRequest request, PluginInvocationContext context)
    {
        EnsureActivated();
        if (request.Source.FixedInput is not null) throw new InvalidOperationException("Backup scope must be edited against the current source.");
        return ValueTask.FromResult(MinecraftSpatialScope.Resolve(request.Source.Folder.Path, request.Selections, SelectedRegionsScopeId, context.OperationCancellation));
    }
}

internal static class MinecraftSpatialScope
{
    internal const string Parameter = "spatialSelectionV1";
    private static readonly string[] Essentials = ["level.dat", "level.dat_old", "icon.png", "resources.zip", "resourcepacks", "advancements", "data", "datapacks", "generated", "playerdata", "players", "stats", "serverconfig"];
    internal static SpatialScopeResult Resolve(string root, IReadOnlyList<SpatialScopeSelection> selections, BackupScopeId scope, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        if (selections.Count is < 1 or > 128) throw new InvalidDataException("Select between 1 and 128 regions of interest.");
        var rules = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var effective = new List<SpatialScopeSelection>();
        var selected = new HashSet<(string World, string Layer, int X, int Z)>();
        var worlds = new Dictionary<string, WorldDescriptor>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            var b = selection.Bounds;
            if (new[] { b.MinX, b.MinY, b.MaxX, b.MaxY }.Any(v => !double.IsFinite(v) || Math.Abs(v) > 30_000_512)
                || b.MinX >= b.MaxX || b.MinY >= b.MaxY) throw new InvalidDataException("Invalid selection coordinates.");
            if (!worlds.TryGetValue(selection.WorldId, out var world))
            {
                var path = SpatialWorldCatalog.PathFor(root, selection.WorldId);
                worlds.Add(selection.WorldId, world = WorldScanner.Scan(root, path, token));
                foreach (var item in Essentials)
                {
                    Add(Path.Combine(path, item));
                    if (!Path.HasExtension(item)) Add(Path.Combine(path, item, "**"));
                }
            }
            var dimension = world.Dimensions.SingleOrDefault(d => d.Id == selection.LayerId)
                ?? throw new InvalidDataException("Selected dimension is no longer available.");
            var minX = (int)Math.Floor(b.MinX / 512); var minZ = (int)Math.Floor(b.MinY / 512);
            var maxX = (int)Math.Ceiling(b.MaxX / 512) - 1; var maxZ = (int)Math.Ceiling(b.MaxY / 512) - 1;
            if ((long)(maxX - minX + 1) * (maxZ - minZ + 1) > 4096) throw new InvalidDataException("Region selection budget exceeded.");
            for (var x = minX; x <= maxX; x++) for (var z = minZ; z <= maxZ; z++)
            {
                if (!selected.Add((selection.WorldId, selection.LayerId, x, z))) continue;
                if (selected.Count > 4096) throw new InvalidDataException("Region selection budget exceeded.");
                foreach (var component in new[] { "region", "entities", "poi" }) Add(Path.Combine(dimension.Root, component, $"r.{x}.{z}.mca"));
            }
            Add(Path.Combine(dimension.Root, "data", "**"));
            foreach (var component in new[] { "region", "entities", "poi" }) Add(Path.Combine(dimension.Root, component, "c.*.*.mcc"));
            effective.Add(selection with { Bounds = new(minX * 512d, minZ * 512d, (maxX + 1d) * 512, (maxZ + 1d) * 512) });
        }
        if (rules.Count > 256) throw new InvalidDataException("Selection exceeds the Host's 256 scope-pattern budget; reduce the selection.");
        var json = JsonSerializer.Serialize(effective);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 32768) throw new InvalidDataException("Selection metadata budget exceeded.");
        return new(scope, new Dictionary<string, string> { [Parameter] = json }, effective.AsReadOnly(), rules.Order(StringComparer.Ordinal).ToArray(), []);
        void Add(string path) => rules.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
    }
}
