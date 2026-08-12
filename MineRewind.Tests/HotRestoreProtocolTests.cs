namespace MineRewind.Tests;

[TestClass]
public sealed class HotRestoreProtocolTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow(" backup.7z ", "backup.7z")]
    [DataRow("世界 1.zip", "世界 1.zip")]
    public void BackupIdAcceptsOnlyAFileName(string? value, string? expected)
    {
        Assert.IsTrue(MinecraftHotRestoreProtocol.TryNormalizeBackupId(value, out var normalized));
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(".")]
    [DataRow("..")]
    [DataRow("../backup.7z")]
    [DataRow("folder/backup.7z")]
    [DataRow("folder\\backup.7z")]
    [DataRow("backup\u0001.7z")]
    public void BackupIdRejectsEmptyTraversalPathsAndControlCharacters(string value)
    {
        Assert.IsFalse(MinecraftHotRestoreProtocol.TryNormalizeBackupId(value, out var normalized));
        Assert.IsNull(normalized);
    }

    [TestMethod]
    public void PartialBackupUsesOverwriteAndFullBackupUsesCleanRestore()
    {
        Assert.AreEqual(
            FolderRewind.Services.BackupService.RestoreMode.Overwrite,
            MinecraftHotRestoreProtocol.ResolveRestoreMode(isPartialBackup: true));
        Assert.AreEqual(
            FolderRewind.Services.BackupService.RestoreMode.Clean,
            MinecraftHotRestoreProtocol.ResolveRestoreMode(isPartialBackup: false));
    }

    [TestMethod]
    public void SessionLockMakesWorldOccupied()
    {
        var root = CreateWorld();
        try
        {
            var sessionLock = Path.Combine(root, "session.lock");
            File.WriteAllText(sessionLock, "locked");

            var occupied = MinecraftHotRestoreProtocol.IsWorldOccupied(
                root,
                path => string.Equals(path, sessionLock, StringComparison.OrdinalIgnoreCase));

            Assert.IsTrue(occupied);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LockedLevelDbFileMakesWorldOccupied()
    {
        var root = CreateWorld();
        try
        {
            var db = Path.Combine(root, "db");
            Directory.CreateDirectory(db);
            var lockedFile = Path.Combine(db, "000001.ldb");
            File.WriteAllText(lockedFile, "locked");

            Assert.IsTrue(MinecraftHotRestoreProtocol.IsWorldOccupied(
                root,
                path => string.Equals(path, lockedFile, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LockProbeFailureFailsSafeWithoutCrashing()
    {
        var root = CreateWorld();
        try
        {
            File.WriteAllText(Path.Combine(root, "session.lock"), "locked");

            Assert.IsFalse(MinecraftHotRestoreProtocol.IsWorldOccupied(
                root,
                _ => throw new IOException("probe failed")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateWorld()
    {
        var root = Path.Combine(Path.GetTempPath(), "MineRewindTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
