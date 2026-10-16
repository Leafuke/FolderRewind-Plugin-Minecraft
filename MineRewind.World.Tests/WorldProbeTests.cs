using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class WorldProbeTests
{
    private string _root = null!;
    [TestInitialize] public void Init() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WorldProbeTests", Guid.NewGuid().ToString("N"))).FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, true);
    private string Source(string name, string contents = "original")
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(path, "level.dat"), contents); return path;
    }
    private async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        var assembly = typeof(WorldProbeTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, "runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, "deps.json"), Path.Combine(AppContext.BaseDirectory, "MineRewind.WorldProbe.dll") }.Concat(args))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    [TestMethod]
    public async Task RoundtripAndIndexProduceIndependentVerifiedResults()
    {
        var source = Source("input"); var output = Path.Combine(_root, "output");
        var result = await Run("roundtrip", "--source", source, "--source-id", "test", "--output", output);
        Assert.AreEqual(0, result.Exit, result.Error);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(output, "reconstructed", "level.dat")));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json")));
        Assert.IsTrue(report.RootElement.GetProperty("Verification").GetProperty("Success").GetBoolean());
        Assert.IsFalse(report.RootElement.GetProperty("ApplicableMerge").GetBoolean());
        var index = await Run("index", "--source", source, "--source-id", "test", "--output", Path.Combine(_root, "index"));
        Assert.AreEqual(0, index.Exit, index.Error);
    }

    [TestMethod]
    public async Task ExistingAndOverlappingOutputAreRejectedWithoutOverwriting()
    {
        var source = Source("input"); var output = Source("existing", "sentinel");
        foreach (var destination in new[] { output, source, Path.Combine(source, "nested"), _root })
        {
            var result = await Run("index", "--source", source, "--source-id", "test", "--output", destination);
            Assert.AreEqual(2, result.Exit);
        }
        Assert.AreEqual("sentinel", File.ReadAllText(Path.Combine(output, "level.dat")));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(source, "level.dat")));
    }

    [TestMethod]
    public async Task ComparisonReportsConflictsAndNeverCreatesMergedWorld()
    {
        var b = Source("base", "base"); var o = Source("ours", "ours"); var t = Source("theirs", "theirs");
        var output = Path.Combine(_root, "compare");
        var result = await Run("compare", "--base", b, "--ours", o, "--theirs", t, "--source-id", "same", "--output", output);
        Assert.AreEqual(1, result.Exit, result.Error);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json")));
        Assert.AreEqual(1, report.RootElement.GetProperty("Comparison").GetProperty("Conflicts").GetInt32());
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "reconstructed")));
        Assert.AreEqual("ours", File.ReadAllText(Path.Combine(o, "level.dat")));
    }

    [TestMethod]
    public async Task IncompleteSemanticInputReturnsAttentionWhilePreservingBytes()
    {
        var source = Source("input"); Directory.CreateDirectory(Path.Combine(source, "region"));
        File.WriteAllBytes(Path.Combine(source, "region", "r.0.0.mca"), []);
        var output = Path.Combine(_root, "output");
        var result = await Run("roundtrip", "--source", source, "--source-id", "same", "--output", output);
        Assert.AreEqual(1, result.Exit, result.Error);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "result.json")));
        Assert.IsTrue(report.RootElement.GetProperty("Verification").GetProperty("Success").GetBoolean());
        Assert.IsFalse(report.RootElement.GetProperty("VerificationPassed").GetBoolean());
    }
}
