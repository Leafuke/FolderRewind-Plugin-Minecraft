using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using MineRewind.Discovery;

namespace MineRewind.Tests;

[TestClass]
public sealed class DiscoveryFixtureTests
{
    [TestMethod]
    public void LauncherFixturesResolveRelativePathsAndMergeSourcesWithoutAmbientReads()
    {
        using var fixture = new DiscoveryFixture();
        var game = fixture.World("workspace/game/saves/World");
        fixture.Text("workspace/hmcl.json", """{"directories":[{"path":"game","name":"HMCL fixture"}]}""");
        fixture.Text("workspace/PCL/Setup.ini", "LaunchFolderSelect:$game");
        fixture.Text("workspace/config.v1.json", JsonSerializer.Serialize(new { LaunchFolders = "Fixture>" + Path.GetDirectoryName(Path.GetDirectoryName(game)) }));
        fixture.Text("workspace/prismlauncher.cfg", "InstanceDir=custom-library");
        fixture.Text("workspace/custom-library/instance/instance.cfg", "name=Prism fixture");
        var prism = fixture.World("workspace/custom-library/instance/minecraft/saves/Prism");
        var modrinth = fixture.World("workspace/profiles/profile/saves/Modrinth");
        var scan = fixture.Scan([fixture.PathOf("workspace"), fixture.PathOf("workspace/game")]);
        var java = scan.Instances.Single(i => i.Worlds.Any(w => w.Path == game));
        foreach (var source in new[] { "user", "hmcl", "pcl", "pclce" }) Assert.Contains(source, java.Sources);
        Assert.HasCount(1, java.Worlds);
        Assert.IsTrue(scan.Instances.Any(i => i.Name == "Prism fixture" && i.Worlds.Single().Path == prism));
        Assert.IsTrue(scan.Instances.Any(i => i.Sources.Contains("modrinth") && i.Worlds.Single().Path == modrinth));
        Assert.IsEmpty(scan.Diagnostics);
    }

    [TestMethod]
    public void KnownLocationFixturesCoverOfficialBedrockNeteaseAndPclRegistry()
    {
        using var fixture = new DiscoveryFixture();
        var pcl = fixture.World("pcl/saves/Pcl");
        var netease = fixture.World("netease/Game/.minecraft/saves/NetEase");
        var bedrock = fixture.World("local/Packages/Microsoft.MinecraftUWP_8wekyb3d8bbwe/LocalState/games/com.mojang/minecraftWorlds/Bedrock");
        Directory.CreateDirectory(Path.Combine(bedrock, "db")); fixture.Text(Path.GetRelativePath(fixture.Root, Path.Combine(bedrock, "levelname.txt")), "Bedrock fixture");
        fixture.World("roaming/MinecraftPE_Netease/minecraftWorlds/NetEasePE");
        fixture.World("roaming/Minecraft Bedrock/Users/user/games/com.mojang/minecraftWorlds/Modern");
        var env = fixture.Environment with { RegistryValue = (path, name) => (path, name) switch
        {
            (@"Software\PCL", "LaunchFolders") => "PCL fixture>" + fixture.PathOf("pcl"),
            (@"Software\Netease\MCLauncher", "DownloadPath") => fixture.PathOf("netease"), _ => null
        } };
        var scan = fixture.Scan([], known: true, env);
        Assert.IsTrue(scan.Instances.Any(i => i.Sources.Contains("pcl2") && i.Worlds.Single().Path == pcl));
        Assert.IsTrue(scan.Instances.Any(i => i.Sources.Contains("netease") && i.Worlds.Single().Path == netease));
        Assert.AreEqual("Bedrock fixture", scan.Instances.Single(i => i.Worlds.Any(w => w.Path == bedrock)).Worlds.Single().Name);
        Assert.AreEqual(3, scan.Instances.Count(i => i.Edition == MinecraftEdition.Bedrock));
        Assert.IsEmpty(scan.Diagnostics);
    }

    [TestMethod]
    public void ManualRootIsIsolatedFromKnownLocationsAndBadConfigurationIsReported()
    {
        using var fixture = new DiscoveryFixture();
        var selected = fixture.World("chosen/saves/Selected"); fixture.World("roaming/.minecraft/saves/Ambient");
        fixture.Text("chosen/config.v1.json", "{broken");
        var env = fixture.Environment with { Folder = _ => throw new AssertFailedException("Manual discovery queried ambient folders."),
            Variable = _ => throw new AssertFailedException("Manual discovery queried environment."),
            RegistryValue = (_, _) => throw new AssertFailedException("Manual discovery queried registry.") };
        var scan = fixture.Scan([fixture.PathOf("chosen")], false, env);
        Assert.AreEqual(selected, scan.Instances.Single().Worlds.Single().Path);
        Assert.IsTrue(scan.Diagnostics.Any(d => d.Code == "minerewind.discovery_config_invalid"));
    }

    [TestMethod]
    public void EmptyDirectoryFanoutPermissionFailuresAndClockAreBounded()
    {
        using var fixture = new DiscoveryFixture();
        for (var i = 0; i < 10; i++) Directory.CreateDirectory(fixture.PathOf("fanout/" + i));
        var limited = fixture.Scan([fixture.PathOf("fanout")], false, fixture.Environment with { MaximumEntries = 3 });
        Assert.IsTrue(limited.Diagnostics.Any(d => d.Code == "minerewind.discovery_budget"));
        var denied = fixture.Scan([fixture.PathOf("fanout")], false, fixture.Environment with { Entries = _ => throw new UnauthorizedAccessException() });
        Assert.IsTrue(denied.Diagnostics.Any(d => d.Code == "minerewind.discovery_directory_unreadable"));
        var elapsed = fixture.Scan([fixture.PathOf("fanout")], false, fixture.Environment with { Clock = new ExpiredClock() });
        Assert.IsTrue(elapsed.Diagnostics.Any(d => d.Code == "minerewind.discovery_budget" && d.Arguments["source"] == "time"));
    }

    private sealed class ExpiredClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, 30);
    }
    private sealed class DiscoveryFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MineRewind.Discovery", Guid.NewGuid().ToString("N"))).FullName;
        internal MinecraftDiscoveryEnvironment Environment => new()
        {
            Folder = f => f switch { System.Environment.SpecialFolder.ApplicationData => PathOf("roaming"),
                System.Environment.SpecialFolder.LocalApplicationData => PathOf("local"), _ => PathOf("user") },
            Variable = _ => null, RegistryValue = (_, _) => null
        };
        internal string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Text(string relative, string contents)
        { var path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, contents); }
        internal string World(string relative) { Text(relative + "/level.dat", "fixture"); return PathOf(relative); }
        internal MinecraftDiscoveryScan Scan(string[] roots, bool known = false, MinecraftDiscoveryEnvironment? environment = null)
            => MinecraftDiscoveryService.Discover(new DiscoveryRequest(roots) { IncludeKnownLocations = known }, default, environment ?? Environment);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
