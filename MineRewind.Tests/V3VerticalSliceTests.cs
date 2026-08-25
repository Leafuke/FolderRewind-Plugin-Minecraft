using System.Text.Json;
using fNbt;
using FolderRewind.Plugin.Abstractions;
using V3Plugin = MineRewind.MinecraftSavesPlugin;

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
    public async Task DiscoveryCatalogMapsWorldCandidatesToMinecraftJava()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var catalog = (IDiscoveryDefinitionCatalog)fixture.Plugin;

        var result = await fixture.Plugin.DiscoverAsync(
            new DiscoveryRequest([world.Root]),
            fixture.Invocation);

        var definition = catalog.Definitions.Single();
        Assert.AreEqual(V3Plugin.MinecraftDefinitionIdentity, definition.DefinitionId);
        Assert.AreEqual("Minecraft: Java Edition", definition.DisplayName);
        Assert.AreEqual(V3Plugin.MinecraftDefinitionIdentity, catalog.ResolveDefinitionId(result.Candidates.Single()));
        Assert.AreNotEqual(V3Plugin.MinecraftDefinitionIdentity, result.Candidates.Single().CandidateId);
    }

    [TestMethod]
    public async Task DiscoveryKeepsMultipleWorldsAsIndependentStableCandidates()
    {
        using var world = TemporaryWorld.Create();
        var secondWorld = Path.Combine(world.Root, ".minecraft", "saves", "World 2");
        Directory.CreateDirectory(secondWorld);
        File.WriteAllText(Path.Combine(secondWorld, "level.dat"), "fixture");
        var fixture = Activate();

        var first = await fixture.Plugin.DiscoverAsync(
            new DiscoveryRequest([world.Root]),
            fixture.Invocation);
        var second = await fixture.Plugin.DiscoverAsync(
            new DiscoveryRequest([world.Root + Path.DirectorySeparatorChar]),
            fixture.Invocation);

        Assert.HasCount(2, first.Candidates);
        CollectionAssert.AreEquivalent(
            first.Candidates.Select(candidate => candidate.CandidateId).ToArray(),
            second.Candidates.Select(candidate => candidate.CandidateId).ToArray());
        Assert.AreEqual(2, first.Candidates.SelectMany(candidate => candidate.ConfigDrafts).Count());
    }

    [TestMethod]
    public async Task ConsistencyLeaseUsesCoordinatedSourceBeforeHostCapture()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var fixture = Activate();
        fixture.Services.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            if (eventName == "handshake")
            {
                await fixture.Plugin.ExecuteAsync(
                    "HANDSHAKE_RESPONSE",
                    new Dictionary<string, string> { ["mod_version"] = "3.0.0" },
                    fixture.Invocation);
            }
            else if (eventName == "pre_hot_backup")
            {
                await fixture.Plugin.ExecuteAsync(
                    "WORLD_SAVED",
                    new Dictionary<string, string>(),
                    fixture.Invocation);
            }
        };
        var (config, folder) = Snapshots(world.WorldPath);

        var lease = await fixture.Plugin.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Prefer),
            fixture.Invocation);

        var snapshotPath = lease.SourcePath;
        Assert.AreNotEqual(world.WorldPath, snapshotPath);
        Assert.IsTrue(File.Exists(Path.Combine(snapshotPath, "level.dat")));
        CollectionAssert.AreEqual(
            new[] { "handshake", "handshake_ack", "pre_hot_backup" },
            fixture.Services.KnotLink.Events.Select(value => value.Name).ToArray());
        Assert.IsEmpty(lease.Diagnostics);
        await lease.DisposeAsync();
        Assert.IsFalse(Directory.Exists(snapshotPath));
    }

    [TestMethod]
    public async Task IncompatibleCompanionFallsBackWithWarningForPreferredBackupConsistency()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var fixture = Activate();
        fixture.Services.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            if (eventName != "handshake") return;
            await fixture.Plugin.ExecuteAsync(
                "HANDSHAKE_RESPONSE",
                new Dictionary<string, string> { ["mod_version"] = "2.9.9" },
                fixture.Invocation);
        };
        var (config, folder) = Snapshots(world.WorldPath);

        await using var lease = await fixture.Plugin.AcquireAsync(
            new BackupConsistencyRequest(config, folder, ConsistencyIntent.Prefer),
            fixture.Invocation);

        Assert.IsTrue(lease.Diagnostics.Any(value =>
            value.Code == "minerewind.consistency_handshake_unavailable"));
        CollectionAssert.AreEqual(
            new[] { "handshake", "handshake_ack" },
            fixture.Services.KnotLink.Events.Select(value => value.Name).ToArray());
    }

    [TestMethod]
    public async Task RestoreCoordinatorRunsContinuationOnceAndRejoinsWithoutOwningHostSafetyBackup()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var fixture = Activate();
        fixture.Services.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            if (eventName == "handshake")
            {
                await fixture.Plugin.ExecuteAsync(
                    "HANDSHAKE_RESPONSE",
                    new Dictionary<string, string> { ["mod_version"] = "3.0.0" },
                    fixture.Invocation);
            }
            else if (eventName == "pre_hot_restore")
            {
                sessionLock.Dispose();
                await fixture.Plugin.ExecuteAsync(
                    "WORLD_SAVE_AND_EXIT_COMPLETE",
                    new Dictionary<string, string>(),
                    fixture.Invocation);
            }
            else if (eventName == "rejoin_world")
            {
                await fixture.Plugin.ExecuteAsync(
                    "REJOIN_RESULT",
                    new Dictionary<string, string> { ["result"] = "success" },
                    fixture.Invocation);
            }
        };
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
        Assert.IsEmpty(fixture.Services.Backups.Requests);
        CollectionAssert.AreEqual(
            new[]
            {
                "handshake",
                "handshake_ack",
                "pre_hot_restore",
                "restore_finished",
                "rejoin_world",
                "hot_restore_complete"
            },
            fixture.Services.KnotLink.Events.Select(value => value.Name).ToArray());
        var restoreFinished = fixture.Services.KnotLink.Events.Single(value => value.Name == "restore_finished");
        var rejoinWorld = fixture.Services.KnotLink.Events.Single(value => value.Name == "rejoin_world");
        var hotRestoreComplete = fixture.Services.KnotLink.Events.Single(value => value.Name == "hot_restore_complete");
        Assert.AreEqual("success", restoreFinished.Arguments["status"]);
        Assert.IsGreaterThanOrEqualTo(
            TimeSpan.FromMilliseconds(2_800),
            rejoinWorld.SentAt - restoreFinished.SentAt,
            "rejoin_world 必须等待模组完成退出世界后的状态切换，不能紧跟 restore_finished 发送。");
        Assert.AreEqual("full_success", hotRestoreComplete.Arguments["status"]);
    }

    [TestMethod]
    public async Task FailedMutationStillRejoins()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var services = new FakeHostServices();
        var fixture = Activate(services);
        fixture.Services.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            if (eventName == "handshake")
            {
                await fixture.Plugin.ExecuteAsync(
                    "HANDSHAKE_RESPONSE",
                    new Dictionary<string, string> { ["mod_version"] = "3.0.0" },
                    fixture.Invocation);
            }
            else if (eventName == "pre_hot_restore")
            {
                sessionLock.Dispose();
                await fixture.Plugin.ExecuteAsync(
                    "WORLD_SAVE_AND_EXIT_COMPLETE",
                    new Dictionary<string, string>(),
                    fixture.Invocation);
            }
            else if (eventName == "rejoin_world")
            {
                await fixture.Plugin.ExecuteAsync(
                    "REJOIN_RESULT",
                    new Dictionary<string, string> { ["result"] = "success" },
                    fixture.Invocation);
            }
        };
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
                    return ValueTask.FromResult(OperationOutcome.Failed);
                }),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Failed, result.Outcome);
        Assert.AreEqual(1, mutationCalls);
        Assert.IsTrue(fixture.Services.KnotLink.Events.Any(value => value.Name == "rejoin_world"));
    }

    [TestMethod]
    public async Task IncompatibleCompanionBlocksActiveRestoreBeforeMutation()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var fixture = Activate();
        fixture.Services.KnotLink.OnSendAsync = async (eventName, _, _) =>
        {
            if (eventName != "handshake") return;
            await fixture.Plugin.ExecuteAsync(
                "HANDSHAKE_RESPONSE",
                new Dictionary<string, string> { ["mod_version"] = "2.9.9" },
                fixture.Invocation);
        };
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

        Assert.AreEqual(OperationOutcome.Blocked, result.Outcome);
        Assert.AreEqual(0, mutationCalls);
        CollectionAssert.AreEqual(
            new[] { "handshake", "handshake_ack", "restore_cancelled" },
            fixture.Services.KnotLink.Events.Select(value => value.Name).ToArray());
    }

    [TestMethod]
    public async Task WarningMutationRemainsVisible()
    {
        using var world = TemporaryWorld.Create();
        var services = new FakeHostServices();
        var fixture = Activate(services);
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.CoordinateAsync(
            new RestoreCoordinatorRequest(
                config,
                folder,
                "history",
                _ => ValueTask.FromResult(OperationOutcome.SuccessWithWarnings)),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, result.Outcome);
    }

    [TestMethod]
    public async Task SelectedRegionsScopeProducesPartialWorldPatterns()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.ResolveAsync(
            new BackupScopeRequest(
                config,
                folder,
                new BackupScopeId(new OwnerId(V3Plugin.PluginIdentity), "selected-regions"),
                Arguments(("regions", "0,0;-1,2"))),
            fixture.Invocation);

        Assert.AreEqual(OperationReadiness.Ready, result.Readiness);
        Assert.Contains("region/r.0.0.mca", result.IncludePatterns);
        Assert.Contains("poi/r.-1.2.mca", result.IncludePatterns);
        Assert.Contains("region/c.*.*.mcc", result.IncludePatterns);
        Assert.Contains("playerdata", result.IncludePatterns);
    }

    [TestMethod]
    public async Task FilePolicyExcludesLiveLockAndKnownDerivedCaches()
    {
        using var world = TemporaryWorld.Create();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.ResolveAsync(
            new FilePolicyRequest(config, folder),
            fixture.Invocation);

        Assert.Contains("session.lock", result.RequiredExclusions);
        Assert.Contains("voxy/**", result.RequiredExclusions);
    }

    [TestMethod]
    public async Task MetadataReadsLevelDatDomainFields()
    {
        using var world = TemporaryWorld.Create();
        WriteLegacyLevelDat(world.WorldPath, "NBT World", gameType: 1, seed: 8675309, xpLevel: 7);
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.ReadAsync(
            new FolderMetadataRequest(config, folder),
            fixture.Invocation);

        Assert.AreEqual("NBT World", result.Values["worldName"]);
        Assert.AreEqual("Creative", result.Values["gameMode"]);
        Assert.AreEqual("8675309", result.Values["seed"]);
        Assert.AreEqual("True", result.Values["hasPlayerData"]);
        Assert.AreEqual("legacy", result.Values["worldFormat"]);
        Assert.IsEmpty(result.Diagnostics);
    }

    [TestMethod]
    public async Task RestoreCoordinatorPreservesLegacyLevelDatPlayerState()
    {
        using var world = TemporaryWorld.Create();
        WriteLegacyLevelDat(world.WorldPath, "Current", gameType: 0, seed: 1, xpLevel: 42);
        var fixture = Activate(preservePlayerData: true);
        var (config, folder) = Snapshots(world.WorldPath);

        var result = await fixture.Plugin.CoordinateAsync(
            new RestoreCoordinatorRequest(
                config,
                folder,
                "history",
                _ =>
                {
                    WriteLegacyLevelDat(world.WorldPath, "Restored", gameType: 0, seed: 1, xpLevel: 3);
                    return ValueTask.FromResult(OperationOutcome.Success);
                }),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Success, result.Outcome);
        var level = new NbtFile();
        level.LoadFromFile(Path.Combine(world.WorldPath, "level.dat"));
        var player = (NbtCompound)((NbtCompound)level.RootTag["Data"]!)["Player"]!;
        Assert.AreEqual(42, ((NbtInt)player["XpLevel"]!).Value);
    }

    [TestMethod]
    public async Task CommandsRouteThroughHostBackupAndRestoreServices()
    {
        var fixture = Activate();
        var folderId = Guid.NewGuid();
        var backup = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hotbackup.active-world"),
                Arguments(("configId", "config"), ("folderId", folderId.ToString("D")))),
            fixture.Invocation);
        var restore = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hotrestore.active-world"),
                Arguments(
                    ("configId", "config"),
                    ("folderId", folderId.ToString("D")),
                    ("historyItemId", "history"))),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Success, backup.Outcome);
        Assert.AreEqual(OperationOutcome.Success, restore.Outcome);
        Assert.HasCount(1, fixture.Services.Backups.Requests);
        Assert.AreEqual(string.Empty, fixture.Services.Backups.Requests.Single().Options.Comment);
        Assert.HasCount(1, fixture.Services.Restores.Requests);
    }

    [TestMethod]
    [DataRow(OperationOutcome.Failed)]
    [DataRow(OperationOutcome.Blocked)]
    [DataRow(OperationOutcome.Canceled)]
    public async Task HotBackupCommandPreservesNonSuccessHostOutcome(OperationOutcome hostOutcome)
    {
        var services = new FakeHostServices();
        services.Backups.NextOutcome = hostOutcome;
        var fixture = Activate(services);

        var result = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hotbackup.active-world"),
                Arguments(("configId", "config"))),
            fixture.Invocation);

        Assert.AreEqual(hostOutcome, result.Outcome);
        Assert.AreNotEqual(OperationOutcome.NoChanges, result.Outcome);
    }

    [TestMethod]
    public async Task DefaultHotkeyCommandsResolveLockedWorldAndLatestHistory()
    {
        using var world = TemporaryWorld.Create();
        File.WriteAllBytes(Path.Combine(world.WorldPath, "session.lock"), [0]);
        using var sessionLock = new FileStream(
            Path.Combine(world.WorldPath, "session.lock"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        fixture.Services.Configs.QueryResults.Add(config);
        fixture.Services.History.Items.Add(new HistoryItemSnapshot(
            "history-latest",
            folder.FolderId,
            folder.Path,
            "latest.7z",
            DateTimeOffset.UtcNow,
            OperationOutcome.Success));

        var backup = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hotbackup.active-world"),
                new Dictionary<string, JsonElement>()),
            fixture.Invocation);
        var restore = await fixture.Plugin.ExecuteAsync(
            new PluginCommandRequest(
                new PluginCommandId(PluginId, "hotrestore.active-world"),
                new Dictionary<string, JsonElement>()),
            fixture.Invocation);

        Assert.AreEqual(OperationOutcome.Success, backup.Outcome);
        Assert.AreEqual(OperationOutcome.Success, restore.Outcome);
        Assert.AreEqual(folder.FolderId, fixture.Services.Backups.Requests.Single().FolderId);
        Assert.AreEqual("history-latest", fixture.Services.Restores.Requests.Single().HistoryId);
    }

    [TestMethod]
    public async Task KnotLinkCurrentSaveCommandsResolveActiveWorldWithoutConfigId()
    {
        using var world = TemporaryWorld.Create();
        using var sessionLock = world.AcquireSessionLock();
        var fixture = Activate();
        var (config, folder) = Snapshots(world.WorldPath);
        fixture.Services.Configs.QueryResults.Add(config);
        fixture.Services.History.Items.Add(new HistoryItemSnapshot(
            "history-latest",
            folder.FolderId,
            folder.Path,
            "latest.7z",
            DateTimeOffset.UtcNow,
            OperationOutcome.Success));

        var backup = await fixture.Plugin.ExecuteAsync(
            "BACKUP",
            new Dictionary<string, string>
            {
                ["current_save"] = "true",
                ["comment"] = "3.0插件修复后测试"
            },
            fixture.Invocation);
        var list = await fixture.Plugin.ExecuteAsync(
            "LIST_BACKUPS",
            new Dictionary<string, string> { ["current_save"] = "true" },
            fixture.Invocation);
        var restore = await fixture.Plugin.ExecuteAsync(
            "RESTORE",
            new Dictionary<string, string>
            {
                ["current_save"] = "true",
                ["file"] = "latest.7z"
            },
            fixture.Invocation);

        await fixture.Services.Backups.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Services.Restores.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(OperationOutcome.Success, backup.Outcome);
        Assert.AreEqual("latest.7z", list.Values["data"].GetString());
        Assert.AreEqual(OperationOutcome.Success, restore.Outcome);
        var backupRequest = fixture.Services.Backups.Requests.Single();
        Assert.AreEqual(folder.FolderId, backupRequest.FolderId);
        Assert.AreEqual("3.0插件修复后测试", backupRequest.Options.Comment);
        Assert.AreEqual("history-latest", fixture.Services.Restores.Requests.Single().HistoryId);
    }

    [TestMethod]
    public void KnotLinkDescriptorsReserveOnlyCurrentSaveVariantsOfCoreCommands()
    {
        var commands = ((IKnotLinkIntegrationCapability)new V3Plugin()).Commands;

        foreach (var command in new[] { "BACKUP", "LIST_BACKUPS", "RESTORE" })
        {
            var descriptor = commands.Single(value => value.Command == command);
            Assert.AreEqual("true", descriptor.RequiredArguments["current_save"]);
        }
    }

    [TestMethod]
    public void CommandDescriptorsDeclareTheArgumentsConsumedAtRuntime()
    {
        var plugin = new V3Plugin();
        var backup = plugin.Commands.Single(command => command.Id.CommandId == "hotbackup.active-world");
        var restore = plugin.Commands.Single(command => command.Id.CommandId == "hotrestore.active-world");

        CollectionAssert.AreEquivalent(
            new[] { "configId", "folderId" },
            backup.ArgumentSchema.GetProperty("properties").EnumerateObject().Select(value => value.Name).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "configId", "folderId", "historyItemId" },
            restore.ArgumentSchema.GetProperty("properties").EnumerateObject().Select(value => value.Name).ToArray());
        Assert.AreEqual("Alt+Ctrl+S", backup.DefaultHotkey);
        Assert.AreEqual("Alt+Ctrl+Z", restore.DefaultHotkey);
        Assert.IsTrue(backup.IsGlobalHotkey);
        Assert.IsTrue(restore.IsGlobalHotkey);
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

    private static ActivatedFixture Activate(FakeHostServices? services = null, bool preservePlayerData = false)
    {
        services ??= new FakeHostServices();
        var plugin = new V3Plugin();
        var activation = new FakeActivationContext(preservePlayerData);
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

    private static void WriteLegacyLevelDat(
        string worldPath,
        string levelName,
        int gameType,
        long seed,
        int xpLevel)
    {
        var player = new NbtCompound("Player")
        {
            new NbtInt("XpLevel", xpLevel),
            new NbtList("Inventory", NbtTagType.Compound),
            new NbtList("Pos", NbtTagType.Double)
            {
                new NbtDouble(1),
                new NbtDouble(64),
                new NbtDouble(1)
            }
        };
        var data = new NbtCompound("Data")
        {
            new NbtString("LevelName", levelName),
            new NbtInt("GameType", gameType),
            new NbtLong("RandomSeed", seed),
            new NbtLong("Time", 24000),
            new NbtLong("DayTime", 12000),
            new NbtLong("LastPlayed", 123456789),
            new NbtInt("DataVersion", 4321),
            player
        };
        var file = new NbtFile(new NbtCompound(string.Empty) { data });
        file.SaveToFile(Path.Combine(worldPath, "level.dat"), NbtCompression.GZip);
    }

    private sealed record ActivatedFixture(
        V3Plugin Plugin,
        FakeHostServices Services,
        PluginInvocationContext Invocation);

    private sealed class FakeActivationContext : IPluginActivationContext
    {
        public FakeActivationContext(bool preservePlayerData)
        {
            Settings = new PluginSettingsSnapshot(
                V3VerticalSliceTests.PluginId,
                new Dictionary<string, JsonElement>
                {
                    ["AutoDiscoverSaves"] = Json("true"),
                    ["PreservePlayerData"] = Json(preservePlayerData ? "true" : "false")
                });
        }

        public PluginId PluginId => V3VerticalSliceTests.PluginId;
        public PluginSettingsSnapshot Settings { get; }
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
        public FakeHistory History { get; } = new();
        IHistoryQueryService IPluginHostServices.History => History;
        public IPluginNotificationService Notifications { get; } = new FakeNotifications();
        IKnotLinkHostService IPluginHostServices.KnotLink => KnotLink;
        public IPluginDataStore DataStore { get; } = new FakeDataStore();
        public IPluginTemporaryStorage TemporaryStorage { get; } = new FakeTemporaryStorage();
        public IPluginLogger Logger { get; } = new FakeLogger();
    }

    private sealed class FakeConfigQuery : IReadOnlyConfigQueryService
    {
        public List<string> FindRequests { get; } = new();
        public List<ConfigSnapshot> QueryResults { get; } = new();
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
        {
            FindRequests.Add(configId);
            return ValueTask.FromResult<ConfigSnapshot?>(new ConfigSnapshot(
                configId,
                new ConfigRevision("fake"),
                Kind,
                "Minecraft",
                Array.Empty<FolderSnapshot>(),
                new Dictionary<StateOwnerId, ProviderStateSnapshot>()));
        }

        public ValueTask<IReadOnlyList<ConfigSnapshot>> QueryAsync(
            ConfigKindRef? kind,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ConfigSnapshot>>(QueryResults
                .Where(value => !kind.HasValue || value.Kind == kind.Value)
                .ToArray());
    }

    private sealed class FakeBackupRequests : IBackupRequestService
    {
        public List<(string ConfigId, Guid? FolderId, BackupRequestOptions Options)> Requests { get; } = new();
        public TaskCompletionSource<bool> Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OperationOutcome NextOutcome { get; set; } = OperationOutcome.Success;
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => RequestAsync(configId, folderId, BackupRequestOptions.Default, cancellationToken);

        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            BackupRequestOptions options,
            CancellationToken cancellationToken)
        {
            Requests.Add((configId, folderId, options));
            Requested.TrySetResult(true);
            return ValueTask.FromResult(NextOutcome);
        }
    }

    private sealed class FakeRestoreRequests : IRestoreRequestService
    {
        public List<(string ConfigId, Guid FolderId, string HistoryId)> Requests { get; } = new();
        public TaskCompletionSource<bool> Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string historyItemId, CancellationToken cancellationToken)
        {
            Requests.Add((configId, folderId, historyItemId));
            Requested.TrySetResult(true);
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class FakeKnotLink : IKnotLinkHostService
    {
        public bool IsAvailable { get; set; } = true;
        public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task>? OnSendAsync { get; set; }
        public List<(string Name, IReadOnlyDictionary<string, string> Arguments, DateTimeOffset SentAt)> Events { get; } = new();
        public async ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            Events.Add((eventName, arguments, DateTimeOffset.UtcNow));
            if (OnSendAsync is not null)
            {
                await OnSendAsync(eventName, arguments, cancellationToken);
            }
        }
    }

    private sealed class FakeHistory : IHistoryQueryService
    {
        public List<HistoryItemSnapshot> Items { get; } = new();
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(Items
                .Where(value => !folderId.HasValue || value.FolderId == folderId)
                .ToArray());
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

        public FileStream AcquireSessionLock()
        {
            var path = Path.Combine(WorldPath, "session.lock");
            File.WriteAllBytes(path, [0]);
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
