using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin
{
    public ValueTask CloseAsync(SpatialPreviewSource source, PluginInvocationContext context)
    {
        _preview.Close(source.Generation);
        return ValueTask.CompletedTask;
    }
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
