using FolderRewind.Models;
using FolderRewind.Services.Plugins;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MineRewind;

public partial class MinecraftSavesPlugin
{
    private const string DiscoveryProviderId = "com.folderrewind.minerewind";
    private const string MinecraftJavaDefinitionId = "minecraft-java";

    public DiscoveryProviderDescriptor Descriptor { get; } = new()
    {
        Id = DiscoveryProviderId,
        DisplayName = "MineRewind",
        Priority = 100,
        IsSpecialized = true
    };

    public Task<DiscoveryProviderResult> DiscoverAsync(
        DiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var roots = ResolveDotMinecraftRoots(request).ToList();
        var installations = new List<GameInstallation>();
        var backupSets = new List<BackupSetCandidate>();
        var diagnostics = new List<DiscoveryDiagnostic>();

        for (var index = 0; index < roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];
            progress?.Report(new DiscoveryProgress
            {
                ProviderId = DiscoveryProviderId,
                Phase = "instances",
                Message = root,
                Completed = index,
                Total = roots.Count
            });
            installations.Add(new GameInstallation
            {
                InstallationId = $"minecraft:{StableId(root)}",
                Store = GameStore.Standalone,
                InstallPath = root,
                LibraryRoot = root,
                Evidence = new[]
                {
                    new DiscoveryEvidence
                    {
                        Confidence = DiscoveryConfidence.High,
                        Kind = "minecraft-root",
                        Description = "A local .minecraft directory exists.",
                        Source = DiscoveryProviderId
                    }
                }
            });
            foreach (var instance in MinecraftInstanceDiscoveryPlanner.DiscoverInstances(
                         root,
                         warning => diagnostics.Add(Warning(warning))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                backupSets.Add(CreateBackupSet(instance, cancellationToken));
            }
        }

        var candidates = backupSets.Count == 0
            ? Array.Empty<DiscoveredGameCandidate>()
            : new[]
            {
                new DiscoveredGameCandidate
                {
                    StableKey = $"{DiscoveryProviderId}:{MinecraftJavaDefinitionId}",
                    Definition = new GameDefinition
                    {
                        ProviderId = DiscoveryProviderId,
                        DefinitionId = MinecraftJavaDefinitionId,
                        DisplayName = "Minecraft: Java Edition",
                        Aliases = new[] { "Minecraft", "Java Edition" },
                        Notes = new[] { "MineRewind validates worlds by the presence of level.dat." }
                    },
                    Installations = installations,
                    BackupSets = backupSets
                }
            };

        return Task.FromResult(new DiscoveryProviderResult
        {
            ProviderId = DiscoveryProviderId,
            Candidates = candidates,
            Diagnostics = diagnostics,
            Statistics = new DiscoveryScanStatistics
            {
                DefinitionsConsidered = 1,
                InstallationsFound = installations.Count,
                ResourcesFound = backupSets.Sum(set => set.Resources.Count),
                Duration = stopwatch.Elapsed
            }
        });
    }

    private static BackupSetCandidate CreateBackupSet(
        MinecraftInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        var resources = new List<BackupResourceCandidate>();
        foreach (var worldPath in instance.WorldPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resources.Add(CreateDirectoryResource(
                worldPath,
                Path.GetFileName(worldPath),
                new[] { "save" },
                "level.dat",
                cancellationToken));
        }
        if (!string.IsNullOrWhiteSpace(instance.ModsPath))
        {
            resources.Add(CreateDirectoryResource(
                instance.ModsPath,
                $"mods ({instance.VersionName})",
                new[] { "config" },
                "mods directory",
                cancellationToken));
        }
        return new BackupSetCandidate
        {
            StableKey = $"{DiscoveryProviderId}:instance:{StableId(instance.InstancePath)}",
            DisplayName = $"Minecraft - {instance.VersionName}",
            SuggestedConfigType = ConfigTypeName,
            Resources = resources
        };
    }

    private static BackupResourceCandidate CreateDirectoryResource(
        string path,
        string displayName,
        IReadOnlyList<string> tags,
        string marker,
        CancellationToken cancellationToken)
    {
        var (count, size) = MeasureDirectory(path, cancellationToken);
        return new BackupResourceCandidate
        {
            ResourceId = $"{DiscoveryProviderId}:directory:{StableId(path)}",
            ProviderId = DiscoveryProviderId,
            ProviderPriority = 100,
            IsSpecializedProvider = true,
            DisplayName = displayName,
            Kind = BackupResourceKind.Directory,
            FixedRoot = MinecraftInstanceDiscoveryPlanner.NormalizePath(path),
            OriginalExpression = MinecraftInstanceDiscoveryPlanner.NormalizePath(path),
            Tags = tags,
            Evidence = new[]
            {
                new DiscoveryEvidence
                {
                    Confidence = DiscoveryConfidence.High,
                    Kind = "minecraft-marker",
                    Description = $"Validated by {marker}.",
                    Source = DiscoveryProviderId
                }
            },
            CurrentMatchCount = count,
            CurrentSizeBytes = size,
            IsSelectedByDefault = count > 0
        };
    }

    private static IEnumerable<string> ResolveDotMinecraftRoots(DiscoveryRequest request)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPossibleRoot(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
            roots);
        foreach (var userRoot in request.UserRoots)
        {
            AddPossibleRoot(userRoot, roots);
            AddPossibleRoot(Path.Combine(userRoot, ".minecraft"), roots);
            var ancestor = MinecraftInstanceDiscoveryPlanner.FindDotMinecraftRoot(userRoot);
            AddPossibleRoot(ancestor, roots);
        }
        if (request.ProviderSettings.TryGetValue(DiscoveryProviderId, out var settings)
            && settings.TryGetValue("roots", out var configuredRoots))
        {
            foreach (var root in configuredRoots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddPossibleRoot(root, roots);
            }
        }
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddPossibleRoot(string? path, ISet<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var normalized = MinecraftInstanceDiscoveryPlanner.NormalizePath(path);
        if (string.Equals(Path.GetFileName(normalized), ".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(normalized);
        }
    }

    private static (int Count, long Size) MeasureDirectory(string path, CancellationToken cancellationToken)
    {
        var count = 0;
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (count, size);
    }

    private static string StableId(string path)
    {
        var normalized = MinecraftInstanceDiscoveryPlanner.NormalizePath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..16];
    }

    private static DiscoveryDiagnostic Warning(string message)
    {
        return new DiscoveryDiagnostic
        {
            Severity = DiscoveryDiagnosticSeverity.Warning,
            Code = "minecraft-scan-warning",
            Message = message,
            ProviderId = DiscoveryProviderId,
            DefinitionId = MinecraftJavaDefinitionId
        };
    }
}
