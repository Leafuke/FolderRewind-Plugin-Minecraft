using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class WorldObservationTests
{
    [TestMethod]
    [DataRow("region")]
    [DataRow("region-file")]
    [DataRow("session.lock")]
    public void WeakOrdinaryMarkersDoNotEstablishAWorld(string marker)
    {
        using var source = new Source();
        if (marker == "region") Directory.CreateDirectory(Path.Combine(source.Path, marker));
        else File.WriteAllText(Path.Combine(source.Path, marker == "region-file" ? "r.0.0.mca" : marker), "ordinary data");
        var observation = WorldObserver.Observe(source.Path);
        Assert.AreEqual(WorldObservationKind.Ordinary, observation.Kind);
        Assert.IsTrue(observation.IsComplete);
        Assert.IsEmpty(observation.Worlds);
    }

    [TestMethod]
    public void MissingPrimaryMetadataRetainsAllWorldsAndTheirEvidence()
    {
        using var source = new Source();
        var occupied = Directory.CreateDirectory(Path.Combine(source.Path, "occupied")).FullName;
        Directory.CreateDirectory(Path.Combine(occupied, "region"));
        using var held = File.Open(Path.Combine(occupied, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var other = Directory.CreateDirectory(Path.Combine(source.Path, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "level.dat_old"), "marker");
        var observation = WorldObserver.Observe(source.Path);
        Assert.AreEqual(WorldObservationKind.ResolvedWorlds, observation.Kind);
        Assert.HasCount(2, observation.Worlds);
        Assert.IsTrue(observation.IsComplete);
        Assert.AreEqual(WorldIdentityEvidence.SessionLock | WorldIdentityEvidence.WorldLayout,
            observation.Worlds.Single(w => w.Path == occupied).Evidence);
        Assert.AreEqual(WorldIdentityEvidence.BackupMetadata, observation.Worlds.Single(w => w.Path == other).Evidence);
        Assert.IsTrue(observation.Issues.All(i => i.Code == "primary_metadata_missing"));
    }

    [TestMethod]
    [DataRow("world", "world_identity_unresolved")]
    [DataRow("../outside", "server_world_outside_source")]
    public void ServerWorldEvidenceCannotBecomeOrdinaryOrEscapeTheSource(string name, string issue)
    {
        using var source = new Source();
        Directory.CreateDirectory(Path.Combine(source.Path, "world"));
        File.WriteAllText(Path.Combine(source.Path, "server.properties"), "level-name=" + name);
        var observation = WorldObserver.Observe(source.Path);
        Assert.AreEqual(WorldObservationKind.WorldEvidenceUnresolved, observation.Kind);
        Assert.IsFalse(observation.IsComplete);
        Assert.IsEmpty(observation.Worlds);
        Assert.IsTrue(observation.Issues.Any(i => i.Code == issue));
    }

    [TestMethod]
    public void ServerConfigurationEvidenceIsAttachedToTheResolvedCandidate()
    {
        using var source = new Source();
        var world = Directory.CreateDirectory(Path.Combine(source.Path, "world")).FullName;
        File.WriteAllText(Path.Combine(world, "level.dat"), "marker");
        File.WriteAllText(Path.Combine(source.Path, "server.properties"), "level-name=world");
        var observation = WorldObserver.Observe(source.Path);
        Assert.IsTrue(observation.Worlds.Single().Evidence.HasFlag(WorldIdentityEvidence.ServerConfiguration));
    }

    [TestMethod]
    public void ObservationDepthBudgetAndCancellationAreExplicit()
    {
        using var source = new Source();
        var current = source.Path;
        for (var index = 0; index < 17; index++) current = Directory.CreateDirectory(Path.Combine(current, "d")).FullName;
        File.WriteAllText(Path.Combine(current, "level.dat"), "marker");
        var observation = WorldObserver.Observe(source.Path);
        Assert.IsFalse(observation.IsComplete);
        Assert.IsTrue(observation.Issues.Any(i => i.Code == "observation_budget"));
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldObserver.Observe(source.Path, new(true)));
    }

    [TestMethod]
    public void ReadFailureRetainsStructuredErrorInsteadOfAnOrdinaryResult()
    {
        using var source = new Source();
        using var held = File.Open(Path.Combine(source.Path, "server.properties"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var observation = WorldObserver.Observe(source.Path);
        Assert.IsFalse(observation.IsComplete);
        var issue = observation.Issues.Single(i => i.Code == "world_observation_failed");
        Assert.AreEqual(typeof(IOException).FullName, issue.ExceptionType);
        Assert.IsNotNull(issue.HResult);
    }

    [TestMethod]
    public void DiscoveredDirectoryLinksAreNotFollowed()
    {
        using var source = new Source(); using var outside = new Source();
        File.WriteAllText(Path.Combine(outside.Path, "level.dat"), "outside marker");
        var link = Path.Combine(source.Path, "linked");
        try { Directory.CreateSymbolicLink(link, outside.Path); }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException)
        { Assert.Inconclusive("Creating test links requires OS support: " + error.Message); }
        try { Assert.IsEmpty(WorldObserver.Observe(source.Path).Worlds); }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    private sealed class Source : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minerewind-observation-" + Guid.NewGuid().ToString("N"));
        public Source() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
