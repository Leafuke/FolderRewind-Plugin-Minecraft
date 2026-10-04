using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
    [TestMethod]
    [DataRow("external")]
    [DataRow("internal")]
    [DataRow("cycle")]
    public async Task SnapshotSkipsJunctionBeforeDescending(string kind)
    {
        using var temporary = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(temporary.Root);
        var (source, world) = CreateSnapshotLayout(temporary.Root, "paper");
        var outside = Path.Combine(temporary.Root, "outside");
        WriteSnapshotFile(outside, "sentinel.txt", "outside must survive");
        var target = kind switch { "external" => outside, "internal" => Path.Combine(world, "region"), _ => source };
        links.Junctions([(Path.Combine(world, "linked"), target)]);
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        string snapshot;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require),
            fixture.Invocation with { OperationCancellation = timeout.Token }))
        {
            snapshot = lease.SourcePath;
            Assert.IsTrue(lease.IsStableSourceView);
            Assert.IsTrue(File.Exists(Path.Combine(snapshot, "world", "region", "r.0.0.mca")));
            Assert.IsFalse(Directory.Exists(Path.Combine(snapshot, "world", "linked")));
            Assert.IsEmpty(Directory.GetFiles(snapshot, "sentinel.txt", SearchOption.AllDirectories));
            var warning = lease.Diagnostics.Single();
            Assert.AreEqual("minerewind.snapshot_links_skipped", warning.Code);
            Assert.AreEqual(DiagnosticSeverity.Warning, warning.Severity);
            Assert.AreEqual("1", warning.Arguments["count"]);
            CollectionAssert.AreEqual(new[] { "world/linked" }, JsonSerializer.Deserialize<string[]>(warning.Arguments["paths"])!);
        }
        Assert.IsFalse(Directory.Exists(snapshot));
        Assert.AreEqual("outside must survive", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SnapshotSkipsFileAndDirectorySymbolicLinks(bool directory)
    {
        using var temporary = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(temporary.Root);
        var (source, _) = CreateSnapshotLayout(temporary.Root, "direct");
        var outside = Path.Combine(temporary.Root, "outside");
        WriteSnapshotFile(outside, "sentinel.txt", "untouched");
        links.Symbolic(Path.Combine(source, "linked"), directory ? outside : Path.Combine(outside, "sentinel.txt"), directory);
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "level.dat")));
        Assert.IsFalse(Path.Exists(Path.Combine(lease.SourcePath, "linked")));
        Assert.AreEqual("1", lease.Diagnostics.Single().Arguments["count"]);
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }

    [TestMethod]
    public async Task SnapshotLinkWarningBoundsPathSamplesButCountsEveryLink()
    {
        using var temporary = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(temporary.Root);
        var (source, _) = CreateSnapshotLayout(temporary.Root, "direct");
        var outside = Path.Combine(temporary.Root, "outside");
        Directory.CreateDirectory(outside);
        links.Junctions(Enumerable.Range(0, 25).Select(index => (Path.Combine(source, "link-" + index), outside)));
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        var warning = lease.Diagnostics.Single();
        Assert.AreEqual("25", warning.Arguments["count"]);
        var paths = JsonSerializer.Deserialize<string[]>(warning.Arguments["paths"])!;
        Assert.HasCount(20, paths);
        Assert.HasCount(20, paths.Distinct().ToArray());
        Assert.IsFalse(Directory.GetDirectories(lease.SourcePath).Any(path => Path.GetFileName(path).StartsWith("link-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SnapshotKeepsHiddenFilesAndPrunesCachesWithoutWarning()
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, "paper");
        WriteSnapshotFile(world, "nested/voxy/locked.bin", "cache");
        WriteSnapshotFile(world, "session.lock", "lock");
        WriteSnapshotFile(world, "hidden.dat", "hidden content");
        WriteSnapshotFile(world, "voxy", "ordinary file, not a cache directory");
        File.SetAttributes(Path.Combine(world, "hidden.dat"), FileAttributes.Hidden);
        using var lockedCache = new FileStream(Path.Combine(world, "nested", "voxy", "locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fixture = Activate();
        var (config, folder) = Snapshots(source);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        Assert.AreEqual("hidden content", File.ReadAllText(Path.Combine(lease.SourcePath, "world", "hidden.dat")));
        Assert.AreEqual("ordinary file, not a cache directory", File.ReadAllText(Path.Combine(lease.SourcePath, "world", "voxy")));
        Assert.IsFalse(Directory.Exists(Path.Combine(lease.SourcePath, "world", "nested", "voxy")));
        Assert.IsFalse(File.Exists(Path.Combine(lease.SourcePath, "world", "session.lock")));
        Assert.IsEmpty(lease.Diagnostics);
    }

    [TestMethod]
    public async Task ExplicitlySelectedRootAliasKeepsItsConfiguredLayout()
    {
        using var temporary = TemporaryWorld.Create();
        using var links = new SnapshotTestLinks(temporary.Root);
        var (source, _) = CreateSnapshotLayout(temporary.Root, "paper");
        var alias = Path.Combine(temporary.Root, "selected-alias");
        links.Junctions([(alias, source)]);
        var fixture = Activate();
        var (config, folder) = Snapshots(alias);
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require), fixture.Invocation);
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "world", "level.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "world_nether", "DIM-1", "region", "r.0.0.mca")));
        Assert.IsEmpty(lease.Diagnostics);
    }

    [TestMethod]
    [DataRow(ConsistencyIntent.Prefer)]
    [DataRow(ConsistencyIntent.Require)]
    public async Task CopyFailureCleansIncompleteSnapshot(ConsistencyIntent intent)
    {
        using var temporary = TemporaryWorld.Create();
        var (source, world) = CreateSnapshotLayout(temporary.Root, "paper");
        var storage = Path.Combine(temporary.Root, "snapshots");
        Directory.CreateDirectory(storage);
        using var lockedFile = new FileStream(Path.Combine(world, "region", "r.0.0.mca"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fixture = Activate(new FakeHostServices { TemporaryStorage = new SnapshotTestStorage(() => storage) });
        var (config, folder) = Snapshots(source);
        if (intent == ConsistencyIntent.Require)
            await Assert.ThrowsExactlyAsync<IOException>(async () => await fixture.Plugin.AcquireAsync(new(config, folder, intent), fixture.Invocation));
        else
        {
            await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, intent), fixture.Invocation);
            Assert.AreEqual(source, lease.SourcePath);
            Assert.IsFalse(lease.IsStableSourceView);
            Assert.AreEqual("minerewind.snapshot_fallback", lease.Diagnostics.Single().Code);
        }
        Assert.IsEmpty(Directory.GetFileSystemEntries(storage));
        Assert.IsTrue(File.Exists(Path.Combine(world, "level.dat")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SnapshotCannotBeCreatedInsideItsSource(bool sameRoot)
    {
        using var temporary = TemporaryWorld.Create();
        var (source, _) = CreateSnapshotLayout(temporary.Root, "direct");
        var target = sameRoot ? source : Path.Combine(source, "snapshot");
        Assert.ThrowsExactly<IOException>(() => MinecraftSnapshotCopier.Copy(source, target, CancellationToken.None));
        Assert.IsTrue(File.Exists(Path.Combine(source, "level.dat")));
        if (!sameRoot) Assert.IsFalse(Directory.Exists(target));
    }

    private sealed class SnapshotTestLinks(string root) : IDisposable
    {
        private readonly List<(string Path, bool Directory)> _links = [];

        public void Junctions(IEnumerable<(string Link, string Target)> entries)
        {
            if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Junction tests require Windows; they must run on Windows CI.");
            var values = entries.Select(entry => new { entry.Link, entry.Target }).ToArray();
            foreach (var value in values) Track(value.Link, true);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
                "$ErrorActionPreference = 'Stop'; foreach ($entry in (ConvertFrom-Json $env:MINEREWIND_TEST_JUNCTIONS)) { New-Item -ItemType Junction -Path $entry.Link -Target $entry.Target | Out-Null }")));
            start.Environment["MINEREWIND_TEST_JUNCTIONS"] = JsonSerializer.Serialize(values);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Creating test junctions timed out.");
            }
            Assert.AreEqual(0, process.ExitCode, error.GetAwaiter().GetResult());
        }

        public void Symbolic(string link, string target, bool directory)
        {
            Track(link, directory);
            try
            {
                if (directory) System.IO.Directory.CreateSymbolicLink(link, target);
                else File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is IOException && (ex.HResult & 0xffff) == 1314))
            {
                Assert.Inconclusive("Symbolic link creation requires permission: " + ex.Message);
            }
        }

        private void Track(string path, bool directory)
        {
            var relative = Path.GetRelativePath(root, path);
            Assert.IsFalse(Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."));
            _links.Add((path, directory));
        }

        public void Dispose()
        {
            // Unlink each entry without recursion before the owning temporary tree is removed.
            foreach (var (path, directory) in _links)
            {
                if (!Path.Exists(path)) continue;
                if (directory) System.IO.Directory.Delete(path, recursive: false);
                else File.Delete(path);
            }
        }
    }
}
