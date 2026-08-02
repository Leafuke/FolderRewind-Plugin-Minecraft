using FolderRewind.Services;

namespace MineRewind
{
    internal static class MinecraftHotRestoreProtocol
    {
        public static bool TryNormalizeBackupId(string? value, out string? normalized)
        {
            normalized = null;
            if (value == null)
            {
                return true;
            }

            string candidate = value.Trim();
            if (candidate.Length == 0
                || candidate == "."
                || candidate == ".."
                || candidate.Contains('/')
                || candidate.Contains('\\')
                || candidate.Any(char.IsControl))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        public static BackupService.RestoreMode ResolveRestoreMode(bool isPartialBackup)
            => isPartialBackup
                ? BackupService.RestoreMode.Overwrite
                : BackupService.RestoreMode.Clean;

        public static bool IsWorldOccupied(string worldPath, Func<string, bool> isFileLocked)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(worldPath);
            ArgumentNullException.ThrowIfNull(isFileLocked);

            try
            {
                var sessionLock = Path.Combine(worldPath, "session.lock");
                if (File.Exists(sessionLock) && isFileLocked(sessionLock))
                {
                    return true;
                }

                var dbDir = Path.Combine(worldPath, "db");
                if (Directory.Exists(dbDir))
                {
                    foreach (var entry in Directory.EnumerateFiles(dbDir))
                    {
                        if (isFileLocked(entry))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
