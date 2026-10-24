namespace MineRewind;

/// <summary>Default backup exclusions. Merge eligibility is proved by the Host against domain dependencies.</summary>
internal static class MinecraftWorldFilePolicy
{
    internal static IReadOnlyList<string> BackupExclusions { get; } = Array.AsReadOnly<string>(
    [
        "session.lock", "**/session.lock", "voxy/**", "**/DistantHorizons.sqlite",
        "**/DistantHorizons.sqlite-shm", "**/DistantHorizons.sqlite-wal"
    ]);
}
