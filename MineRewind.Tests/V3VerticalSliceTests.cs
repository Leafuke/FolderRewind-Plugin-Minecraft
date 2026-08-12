extern alias v3;

using System.Text.Json;
using FolderRewind.Plugin.Abstractions;
using V3Plugin = v3::MineRewind.MinecraftSavesPlugin;

namespace MineRewind.Tests;

[TestClass]
public sealed class V3VerticalSliceTests
{
    private static readonly PluginId PluginId = new(V3Plugin.PluginIdentity);
    private static readonly ConfigKindRef Kind = new(new OwnerId(V3Plugin.PluginIdentity), V3Plugin.MinecraftKindIdentity);

    [TestMethod]
    public async Task DiscoveryReturnsDraftAndNeverMutatesHostConfiguration()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();

        var result = await fixture.Plugin.DiscoverAsync(
            new DiscoveryRequest([world.Root]),
            fixture.Invocation);

        Assert.HasCount(1, result.Candidates);
        var draft = result.Candidates[0].ConfigDrafts.Single();
        Assert.AreEqual(Kind, draft.Kind);
        Assert.AreEqual(world.WorldPath, draft.Folders.Single().Path);
        Assert.IsEmpty(fixture.Services.Configs.FindRequests);
    }

    [TestMethod]
    public async Task ConsistencyLeaseUsesCoordinatedSourceBeforeHostCapture()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);

        await using var lease = await fixture.Plugin.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Prefer),
            fixture.Invocation);

        Assert.AreEqual(world.WorldPath, lease.SourcePath);
        Assert.AreEqual("minebackup.save", fixture.Services.KnotLink.Events.Single().Name);
        Assert.IsEmpty(lease.Diagnostics);
    }

    [TestMethod]
    public async Task RestoreCoordinatorRunsSafetyBackupThenContinuationOnceAndRejoins()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        var mutationCalls = 0;

        var result = await fixture.Plugin.CoordinateAsync(
            new RestoreCoordinatorRequest(
                config,
                folder,
                "history",
                _ =>
                {
                    mutationCalls++;
                    return ValueTask.FromResult(OperationOutcome.Success);
                }),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        Assert.AreEqual(1, mutationCalls);
        Assert.HasCount(1, fixture.Services.Backups.Requests);
        CollectionAssert.AreEqual(
            new[] { "minebackup.save-and-exit", "minebackup.rejoin" },
            fixture.Services.KnotLink.Events.Select(value => value.Name).ToArray());
    }

    [TestMethod]
    public async Task FailedSafetyBackupSkipsMutationAndStillRejoins()
    {
        using var world = TemporaryWorld.Create();
        var services = new FakeHostServices();
        services.Backups.NextOutcome = OperationOutcome.Failed;
        var fixture = Activate(services);
        var (config, folder) = Snapshots(world.WorldPath);
        var mutationCalls = 0;

        var result = await fixture.Plugin.CoordinateAsync(
            new RestoreCoordinatorRequest(
                config,
                folder,
                "history",
                _ =>
                {
                    mutationCalls++;
                    return ValueTask.FromResult(OperationOutcome.Success);
                }),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Failed, result.Outcome);
        Assert.AreEqual(0, mutationCalls);
        Assert.IsTrue(fixture.Services.KnotLink.Events.Any(value => value.Name == "minebackup.rejoin"));
    }

    [TestMethod]
    public async Task WarningSafetyBackupRemainsVisibleAfterSuccessfulMutation()
    {
        using var world = TemporaryWorld.Create();
        var services = new FakeHostServices();
        services.Backups.NextOutcome = OperationOutcome.SuccessWithWarnings;
        var fixture = Activate(services);
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.CoordinateAsync(
            new RestoreCoordinatorRequest(
                config,
                folder,
                "history",
                _ => ValueTask.FromResult(OperationOutcome.Success)),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
    }

    [TestMethod]
    public async Task CommandsRouteThroughHostBackupAndRestoreServices()
    {
        var fixture = Activate();
        var folderId = Guid.NewGuid();
        var backup = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hot-backup"),
                Arguments(("configId", "config"), ("folderId", folderId.ToString("D")))),
            fixture.Invocation);
        var restore = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "quick-restore"),
                Arguments(
                    ("configId", "config"),
                    ("folderId", folderId.ToString("D")),
                    ("historyItemId", "history"))),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Success, backup.Outcome);
        Assert.AreEqual(OperationOutcome.Success, restore.Outcome);
        Assert.HasCount(1, fixture.Services.Backups.Requests);
        Assert.HasCount(1, fixture.Services.Restores.Requests);
    }

    [TestMethod]
    public void CommandDescriptorsDeclareTheArgumentsConsumedAtRuntime()
    {
        var plugin = new V3Plugin();
        var backup = plugin.Commands.Single(command => command.Id.CommandId == "hot-backup");
        var restore = plugin.Commands.Single(command => command.Id.CommandId == "quick-restore");

        CollectionAssert.AreEquivalent(
            new[] { "configId" },
            backup.ArgumentSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "configId", "folderId", "historyItemId" },
            restore.ArgumentSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    [TestMethod]
    public async Task SchemaZeroProviderStateMigratesWithoutInterpretation()
    {
        var fixture = Activate();
        var location = new ProviderStateLocation("config", null);
        var data = Json("{\"MinecraftVersion\":\"1.21.8\",\"Future\":42}");

        var patch = await fixture.Plugin.MigrateAsync(
            new ProviderStateSnapshot(location, new StateOwnerId(V3Plugin.StateOwnerIdentity), 0, data),
            fixture.Invocation);

        Assert.AreEqual(location, patch.Location);
        Assert.AreEqual(1, patch.SchemaVersion);
        Assert.AreEqual(42, patch.Data.GetProperty("Future").GetInt32());
    }

    private static ActivatedFixture Activate(FakeHostServices? services = null)
    {
        services ??= new FakeHostServices();
        var plugin = new V3Plugin();
        var activation = new FakeActivationContext();
        plugin.ActivateAsync(activation, CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreSame(plugin, activation.Capability);
        return new ActivatedFixture(
            plugin,
            services,
            new PluginInvocationContext(PluginId, services, CancellationToken.None, CancellationToken.None));
    }

    private static (ConfigSnapshot Config, FolderSnapshot Folder) Snapshots(string worldPath)
    {
        var folder = new FolderSnapshot(
            Guid.NewGuid(),
            worldPath,
            Path.GetFileName(worldPath),
            new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        var config = new ConfigSnapshot(
            "config",
            new ConfigRevision("revision-1"),
            Kind,
            "Minecraft",
            [folder],
            new Dictionary<StateOwnerId, ProviderStateSnapshot>());
        return (config, folder);
    }

    private static IReadOnlyDictionary<string, JsonElement> Arguments(params (string Key, string Value)[] arguments)
        => arguments.ToDictionary(value => value.Key, value => Json(JsonSerializer.Serialize(value.Value)));

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed record ActivatedFixture(
        V3Plugin Plugin,
        FakeHostServices Services,
        PluginInvocationContext Invocation);

    private sealed class FakeActivationContext : IPluginActivationContext
    {
        public PluginId PluginId => V3VerticalSliceTests.PluginId;
        public PluginSettingsSnapshot Settings { get; } = new(
            V3VerticalSliceTests.PluginId,
            new Dictionary<string, JsonElement> { ["AutoDiscoverSaves"] = Json("true") });
        public IReadOnlyList<ConfigSnapshot> Configs => Array.Empty<ConfigSnapshot>();
        public IPluginCapability? Capability { get; private set; }

        public void RegisterCapability<TCapability>(TCapability capability) where TCapability : class, IPluginCapability
            => Capability = capability;
    }

    private sealed class FakeHostServices : IPluginHostServices
    {
        public FakeConfigQuery Configs { get; } = new();
        public FakeBackupRequests Backups { get; } = new();
        public FakeRestoreRequests Restores { get; } = new();
        public FakeKnotLink KnotLink { get; } = new();
        IReadOnlyConfigQueryService IPluginHostServices.Configs => Configs;
        IBackupRequestService IPluginHostServices.Backups => Backups;
        IRestoreRequestService IPluginHostServices.Restores => Restores;
        public IHistoryQueryService History { get; } = new FakeHistory();
        public IPluginNotificationService Notifications { get; } = new FakeNotifications();
        IKnotLinkHostService IPluginHostServices.KnotLink => KnotLink;
        public IPluginDataStore DataStore { get; } = new FakeDataStore();
        public IPluginTemporaryStorage TemporaryStorage { get; } = new FakeTemporaryStorage();
        public IPluginLogger Logger { get; } = new FakeLogger();
    }

    private sealed class FakeConfigQuery : IReadOnlyConfigQueryService
    {
        public List<string> FindRequests { get; } = new();
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
        {
            FindRequests.Add(configId);
            return ValueTask.FromResult<ConfigSnapshot?>(null);
        }
    }

    private sealed class FakeBackupRequests : IBackupRequestService
    {
        public List<(string ConfigId, Guid? FolderId)> Requests { get; } = new();
        public OperationOutcome NextOutcome { get; set; } = OperationOutcome.Success;
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
        {
            Requests.Add((configId, folderId));
            return ValueTask.FromResult(NextOutcome);
        }
    }

    private sealed class FakeRestoreRequests : IRestoreRequestService
    {
        public List<(string ConfigId, Guid FolderId, string HistoryId)> Requests { get; } = new();
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string historyItemId, CancellationToken cancellationToken)
        {
            Requests.Add((configId, folderId, historyItemId));
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class FakeKnotLink : IKnotLinkHostService
    {
        public bool IsAvailable { get; set; } = true;
        public List<(string Name, IReadOnlyDictionary<string, string> Arguments)> Events { get; } = new();
        public ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            Events.Add((eventName, arguments));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHistory : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(Array.Empty<HistoryItemSnapshot>());
    }

    private sealed class FakeNotifications : IPluginNotificationService
    {
        public ValueTask ShowAsync(string title, string message, DiagnosticSeverity severity, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class FakeDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeTemporaryStorage : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Path.GetTempPath());
    }

    private sealed class FakeLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null) { }
    }

    private sealed class TemporaryWorld : IDisposable
    {
        private TemporaryWorld(string root, string worldPath)
        {
            Root = root;
            WorldPath = worldPath;
        }

        public string Root { get; }
        public string WorldPath { get; }

        public static TemporaryWorld Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "MineRewind.V3.Tests", Guid.NewGuid().ToString("N"));
            var world = Path.Combine(root, ".minecraft", "saves", "World");
            Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(world, "level.dat"), "fixture");
            return new TemporaryWorld(root, Path.GetFullPath(world));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
