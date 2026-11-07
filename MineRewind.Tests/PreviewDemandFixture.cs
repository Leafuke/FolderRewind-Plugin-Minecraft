using System.Runtime.CompilerServices;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    private sealed class PreviewDemandFixture
    {
        internal readonly Guid View = Guid.NewGuid();
        internal long Revision;
        internal readonly Dictionary<PreviewTileIdentity, SpatialPreviewTileRequest> Tiles = [];
        internal (SpatialPreviewDemand Demand, SpatialPreviewTileRequest Request) Add(SpatialPreviewTileRequest request)
        {
            var key = PreviewTileIdentity.Of(request);
            if (!Tiles.ContainsKey(key)) { Tiles.Add(key, request); Revision++; }
            var tiles = Tiles.Values.Select(t => t with { ViewId = View, DemandRevision = Revision }).ToArray();
            return (new(request.Source, View, Revision, tiles), request with { ViewId = View, DemandRevision = Revision });
        }
    }
    private static readonly ConditionalWeakTable<WorldPreviewSession, PreviewDemandFixture> SessionDemands = new();
    private static readonly ConditionalWeakTable<MinecraftSavesPlugin, Dictionary<Guid, PreviewDemandFixture>> PluginDemands = new();
    private static SpatialPreviewTile RenderPreview(WorldPreviewSession session, SpatialPreviewTileRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = SessionDemands.GetOrCreateValue(session);
        lock (state)
        {
            var next = state.Add(request);
            session.UpdateDemand(next.Demand, token);
            return session.Render(next.Request, token);
        }
    }
    private static async ValueTask<SpatialPreviewTile> RenderPluginPreview(MinecraftSavesPlugin plugin, SpatialPreviewTileRequest request, PluginInvocationContext context)
    {
        var states = PluginDemands.GetOrCreateValue(plugin);
        if (!states.TryGetValue(request.Source.Generation, out var state)) states.Add(request.Source.Generation, state = new());
        var next = state.Add(request);
        await plugin.UpdateDemandAsync(next.Demand, context);
        return await plugin.RenderAsync(next.Request, context);
    }
}
