using System.Collections.ObjectModel;
using System.Globalization;
using fNbt;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World;

namespace MineRewind;

public sealed partial class MinecraftSavesPlugin : IStoppedSourceProtectionCapability
{
    private const string StoppedProtection = "StoppedSourceProtection";
    private const string DataVersionKey = "minecraft.dataVersion";
    private const string LayoutKey = "minecraft.layout";
    private const string LoaderKey = "minecraft.loader";
    private const string ModpackKey = "minecraft.modpack";
    private const string DataPacksKey = "minecraft.dataPacks";
    private const string UnsupportedLayout = "minerewind.stopped_source_layout_unsupported";
    private const string IncompleteScope = "minerewind.stopped_source_scope_incomplete";
    private const string UnknownEnvironment = "minerewind.stopped_source_environment_unknown";
    private const string MismatchedEnvironment = "minerewind.stopped_source_environment_mismatch";

    internal Func<string, MinecraftStoppedWorldLock> OpenStoppedWorldLock { get; set; } = MinecraftStoppedWorldLock.Open;

    ValueTask<StoppedSourceProtectionResult> IStoppedSourceProtectionCapability.AcquireAsync(
        StoppedSourceProtectionRequest request, PluginInvocationContext context)
        => new(Task.Run(() => AcquireStoppedSource(request, context.OperationCancellation), context.OperationCancellation));

    private StoppedSourceProtectionResult AcquireStoppedSource(StoppedSourceProtectionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureActivated();
        if (request.Config.Kind != MinecraftKind || request.OperationId == Guid.Empty
            || !Enum.IsDefined(request.Operation) || !Path.IsPathFullyQualified(request.Folder.Path))
            return new(Unknown(UnsupportedLayout), null);
        if (!SupportsStoppedSourceBoundary(request.Boundary))
            return new(Unknown(IncompleteScope), null);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Folder.Path));
        MinecraftStoppedWorldLock? held = null;
        try
        {
            held = OpenStoppedWorldLock(root);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return new(new(StoppedSourceState.InUse, []), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(LockUnknown(root, error), null);
        }

        try
        {
            // Replay starts from previously admitted Root identity. Its files can be between transaction steps.
            var check = request.Operation == StoppedSourceOperation.Replay
                ? new StoppedSourceProtectionCheck(StoppedSourceState.Stopped, [])
                : ObserveStoppedWorld(root, request, token);
            if (check.State != StoppedSourceState.Stopped) return new(check, null);
            token.ThrowIfCancellationRequested();
            held.RequireHeld();
            var lease = new StoppedWorldLease(root, held, check);
            held = null;
            return new(check, lease);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NbtFormatException)
        {
            return new(Unknown(UnsupportedLayout, ("exceptionType", error.GetType().FullName ?? "")), null);
        }
        finally { held?.Dispose(); }
    }

    private static bool SupportsStoppedSourceBoundary(StoppedSourceBoundary boundary)
        => boundary.IsFullScope && boundary.IsBlacklist && !boundary.UseRegex
            && boundary.FilterRules.All(MinecraftWorldFilePolicy.BackupExclusions.Contains)
            && boundary.Paths.ContainsFile("level.dat") && !boundary.Paths.ContainsFile("session.lock");

    private static StoppedSourceProtectionCheck ObserveStoppedWorld(string root, StoppedSourceProtectionRequest request,
        CancellationToken token)
    {
        var observed = MinecraftWorldPathResolver.Observe(root, token);
        if (observed is not { Kind: WorldObservationKind.ResolvedWorlds, Worlds: [{ HasPrimaryMetadata: true } world] }
            || !StringComparer.OrdinalIgnoreCase.Equals(world.Path, root))
            return Unknown(UnsupportedLayout);

        var data = ReadStoppedWorldMetadata(root, token);
        if (data["DataVersion"] is not NbtInt version || !SurfaceReader.SupportsVersion(version.Value))
            return Unknown(UnsupportedLayout, ("reason", "data-version"));
        var family = version.Value >= 4786 ? WorldLayoutFamily.Namespaced : WorldLayoutFamily.Legacy;
        var layout = WorldLayoutReader.Read(root, root, rejectLinks: true, token: token);
        if (!layout.IsComplete || layout.Issues.Count != 0 || layout.Dimensions.Any(dimension => dimension.Family != family)
            || !layout.Dimensions.Any(dimension => dimension.Id == MinecraftPathGrammar.Overworld
                && dimension.RegionFiles.Any(file => file.Component == ChunkComponentKind.Terrain)))
            return Unknown(UnsupportedLayout, ("reason", "dimensions"));

        var boundary = request.Boundary.Paths;
        var budget = new WorldScanBudget();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            foreach (var path in budget.Entries(root, item.Directory, token, rejectLinks: true, item.Depth))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
                if (MinecraftWorldFilePolicy.Excludes(relative, directory)) continue;
                if (directory)
                {
                    // WorldObserver intentionally skips known data subtrees. A second primary marker there is still ambiguous.
                    if (File.Exists(Path.Combine(path, "level.dat"))) return Unknown(UnsupportedLayout, ("reason", "nested-world"));
                    if (!boundary.MayIntersectDirectory(relative)) return Unknown(IncompleteScope);
                    pending.Push((path, item.Depth + 1));
                }
                else if (!boundary.ContainsFile(relative)) return Unknown(IncompleteScope);
            }
        }

        var observations = ReadEnvironmentEvidence(root, data, version.Value, family, token);
        if (request.Operation == StoppedSourceOperation.Apply)
        {
            var expected = request.ExpectedObservations;
            if (observations.Any(pair => expected.TryGetValue(pair.Key, out var value) && value != pair.Value))
                return Unknown(MismatchedEnvironment) with { Observations = observations };
            if (!IsComparableVanilla(observations) || !IsComparableVanilla(expected)
                || !expected.ContainsKey(DataVersionKey) || !expected.ContainsKey(LayoutKey))
                return Unknown(UnknownEnvironment) with { Observations = observations };
        }
        return new(StoppedSourceState.Stopped, []) { Observations = observations };
    }

    private static NbtCompound ReadStoppedWorldMetadata(string root, CancellationToken token)
    {
        using var input = new FileStream(WorldPaths.RequireContained(root, Path.Combine(root, "level.dat")),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        return BoundedNbtReader.Read(input, NbtReadLimits.Display, token, tag =>
            tag.Name is null or "" or "Data" or "DataVersion" or "WasModded" or "ServerBrands" or "DataPacks" or "Enabled")
            .RootTag["Data"] as NbtCompound ?? throw new InvalidDataException("Missing Minecraft Data compound.");
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironmentEvidence(string root, NbtCompound data,
        int version, WorldLayoutFamily family, CancellationToken token)
    {
        var observations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DataVersionKey] = version.ToString(CultureInfo.InvariantCulture),
            [LayoutKey] = family == WorldLayoutFamily.Legacy ? "legacy" : "namespaced"
        };
        if (data["WasModded"] is NbtByte { Value: 1 })
            observations[LoaderKey] = "modded";
        else if (data["WasModded"] is NbtByte { Value: 0 } && data["ServerBrands"] is NbtList brands
            && brands.Count != 0 && brands.All(tag => tag is NbtString { Value: "vanilla" }))
        {
            observations[LoaderKey] = "vanilla";
            observations[ModpackKey] = "none";
        }
        token.ThrowIfCancellationRequested();
        if ((data["DataPacks"] as NbtCompound)?["Enabled"] is NbtList enabled
            && enabled.Count != 0 && enabled.All(tag => tag is NbtString))
        {
            var packsPath = Path.Combine(root, "datapacks");
            var vanilla = enabled.Count == 1 && enabled[0] is NbtString { Value: "vanilla" }
                && (!Directory.Exists(packsPath) || !Directory.EnumerateFileSystemEntries(packsPath).Any());
            observations[DataPacksKey] = vanilla ? "vanilla" : "custom";
        }
        return new ReadOnlyDictionary<string, string>(observations);
    }

    private static bool IsComparableVanilla(IReadOnlyDictionary<string, string> observations)
        => observations.GetValueOrDefault(LoaderKey) == "vanilla" && observations.GetValueOrDefault(ModpackKey) == "none"
            && observations.GetValueOrDefault(DataPacksKey) == "vanilla";

    private static StoppedSourceProtectionCheck Unknown(string code, params (string Key, string Value)[] arguments)
        => new(StoppedSourceState.Unknown, [Diagnostic(code, DiagnosticSeverity.Error, StoppedProtection, arguments)]);

    private static StoppedSourceProtectionCheck LockUnknown(string root, Exception error)
        => new(StoppedSourceState.Unknown, [LockProbeDiagnostic(new(WorldLockState.Unknown, Path.Combine(root, "session.lock"), error),
            DiagnosticSeverity.Error, StoppedProtection)]);

    private sealed class StoppedWorldLease(string root, MinecraftStoppedWorldLock held, StoppedSourceProtectionCheck initialCheck)
        : IStoppedSourceProtectionLease
    {
        public ValueTask<StoppedSourceProtectionCheck> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { held.RequireHeld(); return ValueTask.FromResult(initialCheck); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            { return ValueTask.FromResult(LockUnknown(root, error)); }
        }

        public ValueTask DisposeAsync()
        {
            held.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
