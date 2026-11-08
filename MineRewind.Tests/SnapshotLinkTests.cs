using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Tests;

public sealed partial class V3VerticalSliceTests
{
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
        Assert.IsTrue(MinecraftRegionBackupScope.TryBuild(alias, Path.Combine(alias, "world"),
            new Dictionary<string, string> { ["regions"] = "0,0" }, out var rules, out var scopeError), scopeError);
        CollectionAssert.Contains(rules.ToArray(), "world/region/r.0.0.mca");
        await using var lease = await fixture.Plugin.AcquireAsync(new(config, folder, ConsistencyIntent.Require) { CoordinationOnly = true }, fixture.Invocation);
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "world", "level.dat")));
        Assert.IsTrue(File.Exists(Path.Combine(lease.SourcePath, "world_nether", "DIM-1", "region", "r.0.0.mca")));
        Assert.IsEmpty(lease.Diagnostics);
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
