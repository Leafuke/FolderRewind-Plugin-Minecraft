namespace MineRewind
{
    /// <summary>
    /// Resolves the actual world directory represented by a configured backup root.
    /// The configured root may itself be a world, or it may be a dedicated server root.
    /// </summary>
    public static class MinecraftWorldPathResolver
    {
        private const string DefaultWorldDirectoryName = "world";
        private const string ServerPropertiesFileName = "server.properties";

        public static string? TryResolveWorldPath(string? configuredRootPath)
        {
            if (string.IsNullOrWhiteSpace(configuredRootPath) || !Directory.Exists(configuredRootPath))
            {
                return null;
            }

            try
            {
                var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRootPath));
                if (HasLevelDat(rootPath))
                {
                    return rootPath;
                }

                string serverPropertiesPath = Path.Combine(rootPath, ServerPropertiesFileName);
                if (File.Exists(serverPropertiesPath))
                {
                    if (!TryReadLevelName(serverPropertiesPath, out string? configuredLevelName))
                    {
                        return null;
                    }

                    if (!string.IsNullOrWhiteSpace(configuredLevelName))
                    {
                        var configuredWorldPath = TryResolveChildPath(rootPath, configuredLevelName);
                        return configuredWorldPath != null && HasLevelDat(configuredWorldPath)
                            ? configuredWorldPath
                            : null;
                    }
                }

                var defaultWorldPath = Path.Combine(rootPath, DefaultWorldDirectoryName);
                return HasLevelDat(defaultWorldPath) ? defaultWorldPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasLevelDat(string path)
        {
            return File.Exists(Path.Combine(path, "level.dat"));
        }

        private static bool TryReadLevelName(
            string serverPropertiesPath,
            out string? levelName)
        {
            levelName = null;

            try
            {
                foreach (var rawLine in File.ReadLines(serverPropertiesPath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex < 0)
                    {
                        continue;
                    }

                    var key = line[..separatorIndex].Trim();
                    if (string.Equals(key, "level-name", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line[(separatorIndex + 1)..].Trim();
                        levelName = value.Length == 0 ? null : value;
                        return true;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryResolveChildPath(string rootPath, string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                return null;
            }

            try
            {
                var candidatePath = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.Combine(rootPath, relativePath)));
                var relativeCandidate = Path.GetRelativePath(rootPath, candidatePath);

                if (Path.IsPathRooted(relativeCandidate)
                    || string.Equals(relativeCandidate, "..", StringComparison.Ordinal)
                    || relativeCandidate.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || relativeCandidate.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    return null;
                }

                return candidatePath;
            }
            catch
            {
                return null;
            }
        }
    }
}
