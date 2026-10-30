using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

[TestClass]
public sealed class SnapshotBudgetTests
{
    [TestMethod]
    public async Task PreflightCountsDirectoriesAndCopyCannotGrowBeyondReservedSpace()
    {
        var root = Path.Combine(Path.GetTempPath(), "SnapshotBudget", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); var target = Path.Combine(root, "copy");
        Directory.CreateDirectory(Path.Combine(source, "empty")); File.WriteAllText(Path.Combine(source, "level.dat"), "data");
        try
        {
            var limits = new BackupSourceViewLimits(1024, 3);
            var bytes = MinecraftSnapshotCopier.InspectSpace(source, limits, default);
            Assert.AreEqual(8196L, bytes);
            Assert.ThrowsExactly<IOException>(() => MinecraftSnapshotCopier.InspectSpace(source, limits with { MaximumBytes = 3 }, default));
            Assert.ThrowsExactly<IOException>(() => MinecraftSnapshotCopier.InspectSpace(source, limits with { MaximumEntries = 2 }, default));
            File.WriteAllBytes(Path.Combine(source, "level.dat"), new byte[32]);
            Assert.ThrowsExactly<IOException>(() => MinecraftSnapshotCopier.Copy(source, target, default, limits, bytes));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() => MinecraftSnapshotCopier.Copy(source, target, canceled.Token, limits, bytes)));
        }
        finally { Directory.Delete(root, true); }
    }
}
