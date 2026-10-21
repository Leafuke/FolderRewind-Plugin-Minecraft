namespace MineRewind;

internal enum WorldLockState { Released, InUse, Unknown }

internal sealed record WorldLockProbeResult(WorldLockState State, string LockPath, Exception? Error = null)
{
    public string? ExceptionType => Error?.GetType().FullName;
    public string? HResult => Error is null ? null : $"0x{Error.HResult:X8}";
    public string? Message => Error?.Message;
}

internal static class MinecraftWorldLockProbe
{
    // The world root was resolved before probing. A vanished root is not a missing lock file.
    internal static WorldLockProbeResult Inspect(string worldPath, Func<string, Stream>? open = null)
    {
        var lockPath = Path.Combine(worldPath, "session.lock");
        try
        {
            using var stream = open is null
                ? new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None) : open(lockPath);
            return new(WorldLockState.Released, lockPath);
        }
        catch (FileNotFoundException ex)
        {
            try
            {
                if ((File.GetAttributes(worldPath) & FileAttributes.Directory) == 0)
                    return new(WorldLockState.Unknown, lockPath, ex);
                return new(WorldLockState.Released, lockPath);
            }
            catch (Exception rootError) when (rootError is IOException or UnauthorizedAccessException)
            { return new(WorldLockState.Unknown, lockPath, rootError); }
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { return new(WorldLockState.InUse, lockPath, ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(WorldLockState.Unknown, lockPath, ex); }
    }
}
