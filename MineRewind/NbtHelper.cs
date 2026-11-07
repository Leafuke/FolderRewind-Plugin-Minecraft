namespace MineRewind;

/// <summary>Compatibility entry for the controlled restore probe; only returns Host staging proposals.</summary>
public static class NbtHelper
{
    public static Task<IReadOnlyList<FolderRewind.Plugin.Abstractions.RestoreStagedFileProposal>> PreparePlayerDataAsync(
        FolderRewind.Plugin.Abstractions.IVersionMetadataSourceView current,
        FolderRewind.Plugin.Abstractions.IVersionMetadataSourceView target, CancellationToken token)
        => NbtPlayerPreservation.PrepareAsync(current, target, token);
}
