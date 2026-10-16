namespace MineRewind;

/// <summary>Copies the configured source without descending into internal filesystem links.</summary>
internal static class MinecraftSnapshotCopier
{
    internal sealed record CopyResult(int SkippedLinkCount, IReadOnlyList<string> SkippedLinkPaths);

    internal static CopyResult Copy(string source, string target, CancellationToken cancellationToken)
    {
        using var stage = MineRewind.World.Semantic.SemanticDiagnostics.Source.StartActivity("consistency.snapshot");
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (IsWithin(source, target))
            throw new IOException("The snapshot directory must be outside the configured source.");

        var skippedCount = 0;
        var skippedPaths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(source);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The explicitly selected root keeps its existing alias semantics. Internal links never enter the tree.
            if (directory != source && IsLink(directory))
            {
                Skip(directory);
                continue;
            }
            var relativeDirectory = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Destination(relativeDirectory));
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Skip(entry);
                    continue;
                }
                var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                var destination = Destination(relative);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (IsDerivedCache(relative, isDirectory)) continue;
                if (isDirectory)
                {
                    pending.Push(entry);
                    continue;
                }
                if (string.Equals(Path.GetFileName(entry), "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                // Recheck immediately before copying; this is not a handle-based defense against hostile races.
                if (IsLink(entry))
                {
                    Skip(entry);
                    continue;
                }
                var stamp = File.GetLastWriteTimeUtc(entry);
                using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    var buffer = new byte[65536]; int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, count);
                    }
                }
                File.SetLastWriteTimeUtc(destination, stamp);
            }
        }
        return new(skippedCount, skippedPaths.ToArray());

        void Skip(string path)
        {
            skippedCount++;
            if (skippedPaths.Count < 20) skippedPaths.Add(Path.GetRelativePath(source, path).Replace('\\', '/'));
        }

        string Destination(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Contains(".."))
                throw new IOException("Snapshot entry escaped the configured source.");
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!IsWithin(target, destination)) throw new IOException("Snapshot entry escaped the snapshot directory.");
            return destination;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) && !relative.Replace('\\', '/').Split('/').Contains("..");
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsDerivedCache(string relativePath, bool isDirectory)
        => (isDirectory && string.Equals(Path.GetFileName(relativePath), "voxy", StringComparison.OrdinalIgnoreCase))
           || relativePath.StartsWith("voxy/", StringComparison.OrdinalIgnoreCase)
           || relativePath.Contains("/voxy/", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite-shm", StringComparison.OrdinalIgnoreCase)
           || relativePath.EndsWith("DistantHorizons.sqlite-wal", StringComparison.OrdinalIgnoreCase);
}
