using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace MineRewind.Discovery;

internal enum MinecraftEdition { Java, Bedrock }
internal enum MinecraftLocationKind { JavaGameRoot, WorldsDirectory, WorldDirectory }
internal sealed record MinecraftLocation(string Path, MinecraftLocationKind Kind,
    MinecraftEdition Edition, string? Name, string Source);
internal sealed record MinecraftDiscoveredWorld(string Path, string Name);
internal sealed class MinecraftDiscoveredInstance(string path, string name, MinecraftEdition edition)
{
    public string Path { get; } = path;
    public string Name { get; set; } = name;
    public MinecraftEdition Edition { get; } = edition;
    public List<MinecraftDiscoveredWorld> Worlds { get; } = [];
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string ModsPath { get; set; } = string.Empty;
}

internal sealed class MinecraftDiscoveryBudgetException(string reason) : Exception
{
    public string Reason { get; } = reason;
}

internal sealed class MinecraftDiscoveryContext(CancellationToken cancellationToken)
{
    public const int TextLimit = 1024 * 1024;
    public const int InstanceLimit = 500;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    public List<PluginDiagnostic> Diagnostics { get; } = [];

    public void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Finish before the Host's 30-second deadline so completed candidates can be returned.
        if (_elapsed.Elapsed >= TimeSpan.FromSeconds(29)) throw new MinecraftDiscoveryBudgetException("time");
    }

    public void Warn(string code, string source, string path)
        => Diagnostics.Add(new(code, DiagnosticSeverity.Warning, "Discovery",
            MinecraftSavesPlugin.PluginIdentity, new Dictionary<string, string>
            {
                ["source"] = source, ["path"] = path
            }));

    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    public IEnumerable<string> Directories(string path, string source)
    {
        Check();
        if (!Directory.Exists(path)) yield break;
        IEnumerator<string> iterator;
        try { iterator = Directory.EnumerateDirectories(path).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("minerewind.discovery_directory_unreadable", source, path);
            yield break;
        }
        using (iterator)
        {
            while (true)
            {
                Check();
                bool next;
                try { next = iterator.MoveNext(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn("minerewind.discovery_directory_unreadable", source, path);
                    yield break;
                }
                if (!next) yield break;
                yield return iterator.Current;
            }
        }
    }

    public string? ReadText(string path, string source)
    {
        Check();
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > TextLimit)
            {
                Warn("minerewind.discovery_config_too_large", source, path);
                return null;
            }
            var buffer = new byte[(int)stream.Length + 1];
            var used = 0;
            while (used < buffer.Length)
            {
                Check();
                var count = stream.Read(buffer, used, buffer.Length - used);
                if (count == 0) break;
                used += count;
            }
            if (used > TextLimit || (used == buffer.Length && stream.ReadByte() != -1))
            {
                Warn("minerewind.discovery_config_changed", source, path);
                return null;
            }
            using var reader = new StreamReader(new MemoryStream(buffer, 0, used), Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn("minerewind.discovery_config_unreadable", source, path);
            return null;
        }
    }

    public JsonDocument? ReadJson(string path, string source)
    {
        var text = ReadText(path, source);
        if (text is null) return null;
        try { return JsonDocument.Parse(text, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException)
        {
            Warn("minerewind.discovery_config_invalid", source, path);
            return null;
        }
    }

    public string? ReadIni(string path, string key, string source, char separator = '=')
    {
        var text = ReadText(path, source);
        if (text is null) return null;
        string? result = null;
        foreach (var raw in text.Split('\n'))
        {
            Check();
            var line = raw.Trim();
            var split = line.IndexOf(separator);
            if (line.StartsWith('#') || line.StartsWith(';') || split <= 0) continue;
            if (string.Equals(line[..split].Trim(), key, StringComparison.OrdinalIgnoreCase))
                result = line[(split + 1)..].Trim().Trim('"', '\'');
        }
        return result;
    }

    public static string? String(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
