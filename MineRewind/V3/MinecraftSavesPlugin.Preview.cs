using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    public ValueTask UpdateDemandAsync(SpatialPreviewDemand demand, PluginInvocationContext context)
    {
        EnsureActivated(); _preview.UpdateDemand(demand, context.OperationCancellation);
        return ValueTask.CompletedTask;
    }
    public ValueTask<SpatialPreviewNavigation> GetNavigationTargetsAsync(SpatialPreviewSource source, PluginInvocationContext context)
    {
        EnsureActivated();
        return new(Task.Run(() => _preview.Navigation(source, context.OperationCancellation), context.OperationCancellation));
    }
    public ValueTask CloseAsync(SpatialPreviewSource source, PluginInvocationContext context)
        => new(_preview.CloseAsync(source.Generation));
    private readonly WorldPreviewProvider _preview = new();
    public ValueTask<SpatialPreviewDescription> DescribeAsync(SpatialPreviewSource source, PluginInvocationContext context)
    {
        EnsureActivated();
        return new(Task.Run(() => _preview.Describe(source, context.OperationCancellation), context.OperationCancellation));
    }
    public ValueTask<SpatialPreviewTile> RenderAsync(SpatialPreviewTileRequest request, PluginInvocationContext context)
    {
        EnsureActivated();
        return new(Task.Run(() => _preview.Render(request, context.OperationCancellation), context.OperationCancellation));
    }
    public ValueTask<SpatialPreviewPoint> InspectAsync(SpatialPreviewPointRequest request, PluginInvocationContext context)
    {
        EnsureActivated();
        return new(Task.Run(() => _preview.Inspect(request, context.OperationCancellation), context.OperationCancellation));
    }
}
