using System.Diagnostics;

namespace MineRewind;

// Keeps the NBT codec independent from FolderRewind Host internals. Invocation-level
// failures are still returned as structured plugin diagnostics by the capability.
internal static class LogService
{
    public static void LogInfo(string message, string source)
        => Trace.TraceInformation("[{0}] {1}", source, message);

    public static void LogWarning(string message, string source)
        => Trace.TraceWarning("[{0}] {1}", source, message);

    public static void LogError(string message, string source, Exception? exception = null)
        => Trace.TraceError("[{0}] {1}{2}", source, message, exception is null ? string.Empty : $" {exception}");
}
