using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RedemandedCompletedTileDoesNotBlockNewTile(bool renderCachedTile)
    {
        using var fixture = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", "r.0.0.mca"), 0);
        WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", "r.8.0.mca"), 0);
        var (config, folder) = Snapshots(fixture.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid()) { CacheBudgetBytes = 1024 * 1024 };
        var session = new WorldPreviewSession(source.CacheBudgetBytes);
        try
        {
            session.Describe(source, default);
            var view = Guid.NewGuid();
            var cached = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 16, 7)
                { ViewId = view, DemandRevision = 1 };
            session.UpdateDemand(new(source, view, 1, [cached]), default);
            var initial = session.Render(cached, default);
            for (var round = 0; !initial.IsFinal && round < 16; round++) initial = session.Render(cached, default);
            Assert.IsTrue(initial.IsFinal);

            session.UpdateDemand(new(source, view, 2, []), default);
            cached = cached with { DemandRevision = 3 };
            var next = cached with { OriginX = 4096 };
            session.UpdateDemand(new(source, view, 3, [cached, next]), default);
            // With no Render for the first tile, the Host is reusing its already uploaded bitmap.
            if (renderCachedTile) Assert.IsTrue(session.Render(cached, default).IsFinal);
            var final = false;
            for (var round = 0; !final && round < 16; round++)
            {
                final = session.Render(next, default).IsFinal;
                Assert.IsLessThanOrEqualTo(source.CacheBudgetBytes, session.CacheBytes);
            }
            Assert.IsTrue(final, "A completed tile returning to the demand blocked the new tile.");
        }
        finally { session.Clear(); }
        Assert.AreEqual(0L, session.CacheBytes);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(5)]
    public void MultipleCoarseTilesConvergeWithoutExceedingOneMiB(int count)
    {
        using var fixture = TemporaryWorld.Create();
        for (var tile = 0; tile < count; tile++)
            for (var z = 0; z < 9; z++) for (var x = 0; x < 9; x++)
                WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{tile * 16 + x}.{z}.mca"), 0);
        var (config, folder) = Snapshots(fixture.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid()) { CacheBudgetBytes = 1024 * 1024 };
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var view = Guid.NewGuid();
        var requests = Enumerable.Range(0, count).Select(i => new SpatialPreviewTileRequest(source, "minecraft:overworld", i * 8192, 0, 32, 7)
            { ViewId = view, DemandRevision = 1 }).ToArray();
        session.UpdateDemand(new(source, view, 1, requests), default);
        var final = new bool[count]; var passes = 0;
        for (; passes < 64 && !final.All(v => v); passes++)
            for (var i = 0; i < count; i++)
            {
                // Continue re-querying completed tiles too: cache misses must not starve old waiting jobs.
                final[i] |= session.Render(requests[i], default).IsFinal;
                Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
            }
        Assert.IsTrue(final.All(v => v), $"Not all {count} tiles completed in {passes} rounds.");
        session.Clear(); Assert.AreEqual(0L, session.CacheBytes);
    }
    [TestMethod]
    public void SharedDemandSurvivesOneViewReleaseAndRejectsLateRenderAfterLastRelease()
    {
        using var fixture = TemporaryWorld.Create();
        for (var z = 0; z < 9; z++) for (var x = 0; x < 9; x++)
            WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{x}.{z}.mca"), 0);
        var (config, folder) = Snapshots(fixture.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var a = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 32, 7) { ViewId = Guid.NewGuid(), DemandRevision = 1 };
        var b = a with { ViewId = Guid.NewGuid() };
        session.UpdateDemand(new(source, a.ViewId, 1, [a]), default);
        session.UpdateDemand(new(source, b.ViewId, 1, [b]), default);
        Assert.IsFalse(session.Render(a, default).IsFinal);
        session.UpdateDemand(new(source, a.ViewId, 2, []), default);
        Assert.IsTrue(session.Render(b, default).IsFinal);
        session.UpdateDemand(new(source, b.ViewId, 2, []), default);
        Assert.ThrowsExactly<OperationCanceledException>(() => session.Render(b, default));
        session.Clear();
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.UpdateDemand(new(source, b.ViewId, 3, [b]), default));
    }
    [TestMethod]
    [DataRow(16, 0, 961)]
    [DataRow(-16, -1, 1)]
    public void CoarseStatisticsExcludeTheOnlyRecordOutsideTheTile(int origin, int region, int expectedAbsent)
    {
        using var fixture = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{region}.{region}.mca"), 1);
        var (config, folder) = Snapshots(fixture.WorldPath);
        var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(); session.Describe(source, default);
        var tile = CompletePreview(session, new(source, "minecraft:overworld", origin, origin, 16, 7));
        Assert.AreEqual(-1, tile.BgraPremultiplied.Span.IndexOfAnyExcept((byte)0));
        Assert.AreEqual(SpatialPreviewContentState.NoData, tile.ContentState);
        Assert.AreEqual(0, tile.Statistics!.Pending);
        Assert.AreEqual(expectedAbsent, tile.Statistics.NoData);
    }
    [TestMethod]
    public void OversizedWorkIsTerminalUnknownAndCallerCancellationDoesNotReleaseDemand()
    {
        using var fixture = TemporaryWorld.Create();
        for (var z = 0; z < 9; z++) for (var x = 0; x < 9; x++)
            WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{x}.{z}.mca"), 0);
        var (config, folder) = Snapshots(fixture.WorldPath); var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var tile = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 32, 7) { ViewId = Guid.NewGuid(), DemandRevision = 1 };
        var small = new WorldPreviewSession(128 * 1024); small.Describe(source, default); small.UpdateDemand(new(source, tile.ViewId, 1, [tile]), default);
        var result = small.Render(tile, default);
        Assert.IsTrue(result.IsFinal); Assert.AreEqual(SpatialPreviewContentState.Unknown, result.ContentState);
        Assert.AreEqual("preview.budget", result.Diagnostics.Single().Code); Assert.IsLessThanOrEqualTo(128L * 1024, small.CacheBytes); small.Clear();
        var normal = new WorldPreviewSession(1024 * 1024); normal.Describe(source, default); normal.UpdateDemand(new(source, tile.ViewId, 1, [tile]), default);
        Assert.IsFalse(normal.Render(tile, default).IsFinal);
        Assert.ThrowsExactly<OperationCanceledException>(() => normal.Render(tile, new(true)));
        Assert.IsTrue(normal.Render(tile, default).IsFinal); normal.Clear();
    }
    [TestMethod]
    public async Task ConcurrentTileCallsAndGenerationCloseReturnAllReservations()
    {
        using var fixture = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", "r.0.0.mca"), 1);
        var (config, folder) = Snapshots(fixture.WorldPath); var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var tile = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 1, 7) { ViewId = Guid.NewGuid(), DemandRevision = 1 };
        session.UpdateDemand(new(source, tile.ViewId, 1, [tile]), default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => session.Render(tile, default))));
        Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
        session.Clear(); Assert.AreEqual(0L, session.CacheBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Render(tile, default));
    }
    [TestMethod]
    public void ReturnedRasterAndSingleDecodeAllocationsAreMeasuredSeparatelyFromRetainedBudget()
    {
        using var fixture = TemporaryWorld.Create();
        WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", "r.0.0.mca"), 1);
        var (config, folder) = Snapshots(fixture.WorldPath); var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var start = GC.GetAllocatedBytesForCurrentThread();
        session.Inspect(new(source, "minecraft:overworld", 0, 0, 7), default);
        var decoding = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.AreEqual(1L, session.DecodedChunks);
        Assert.IsLessThan(64L * 1024 * 1024, decoding);
        var tile = CompletePreview(session, new(source, "minecraft:overworld", 0, 0, 1, 7));
        Assert.AreEqual(262144, tile.BgraPremultiplied.Length);
        Assert.IsLessThanOrEqualTo(1024L * 1024, session.CacheBytes);
        Console.WriteLine($"Single decode cumulative allocations={decoding}; returned raster={tile.BgraPremultiplied.Length}; retained ledger={session.CacheBytes}. Allocations are not a peak-memory measurement.");
        session.Clear();
    }
    [TestMethod]
    public async Task ClosingDuringConcurrentWorkCancelsAndDrainsTheWholeGeneration()
    {
        using var fixture = TemporaryWorld.Create();
        for (var z = 0; z < 20; z++) for (var x = 0; x < 20; x++)
            WritePreviewRegion(Path.Combine(fixture.WorldPath, "region", $"r.{x}.{z}.mca"), 0);
        var (config, folder) = Snapshots(fixture.WorldPath); var source = new SpatialPreviewSource(config, folder, Guid.NewGuid());
        var session = new WorldPreviewSession(1024 * 1024); session.Describe(source, default);
        var tile = new SpatialPreviewTileRequest(source, "minecraft:overworld", 0, 0, 4096, 7) { ViewId = Guid.NewGuid(), DemandRevision = 1 };
        session.UpdateDemand(new(source, tile.ViewId, 1, [tile]), default);
        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { session.Render(tile, default); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        })).ToArray();
        Assert.IsTrue(SpinWait.SpinUntil(() => session.ActiveCalls > 0, TimeSpan.FromSeconds(5)));
        try { await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            Assert.Fail($"Close did not drain: active={session.ActiveCalls}; retained={session.CacheBytes}; calls={string.Join(';', calls.Select(c => c.Status + ":" + c.Exception))}");
        }
        await Task.WhenAll(calls);
        Assert.AreEqual(0, session.ActiveCalls); Assert.AreEqual(0L, session.CacheBytes);
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Describe(source, default));
    }
}
