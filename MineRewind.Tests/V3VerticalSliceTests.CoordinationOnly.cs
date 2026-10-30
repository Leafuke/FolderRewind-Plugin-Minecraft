using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    public async Task CoordinationOnlyDoesNotReadOrCopyLockedWorldContent()
    {
        using var world = TemporaryWorld.Create();
        var data = Path.Combine(world.WorldPath, "busy.bin"); File.WriteAllText(data, "busy");
        using var busy = new FileStream(data, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fixture = Activate(); var (config, folder) = Snapshots(world.WorldPath);
        Assert.IsTrue(fixture.Plugin.SupportsCoordinationOnly);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Prefer)
            { CoordinationOnly = true }, fixture.Invocation);
        Assert.AreEqual(world.WorldPath, lease.SourcePath);
        Assert.IsFalse(lease.IsStableSourceView);
        Assert.IsEmpty(lease.Diagnostics);
        var required = await fixture.Plugin.GetRequiredSourcePathsAsync(config, folder, fixture.Invocation);
        CollectionAssert.AreEqual(new[] { "level.dat" }, required.ToArray());
    }

    [TestMethod]
    public async Task CoordinationOnlyWarningIdentifiesTheActualLockAndView()
    {
        using var world = TemporaryWorld.Create();
        using var held = world.AcquireSessionLock();
        var fixture = Activate(); fixture.Services.KnotLink.IsAvailable = false;
        var (config, folder) = Snapshots(world.WorldPath);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Prefer)
            { CoordinationOnly = true }, fixture.Invocation);
        var warning = lease.Diagnostics.Single(d => d.Code == "minerewind.consistency_raw_source");
        Assert.AreEqual(Path.Combine(world.WorldPath, "session.lock"), warning.Arguments["lockPath"]);
        Assert.AreEqual("InUse", warning.Arguments["lockState"]);
        Assert.AreEqual("host_managed", warning.Arguments["sourceView"]);
        Assert.AreEqual(world.WorldPath, lease.SourcePath);
    }
}
