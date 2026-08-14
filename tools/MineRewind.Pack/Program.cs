using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: MineRewind.Pack <build-output-directory> <output.frplugin>");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
var manifestPath = Path.Combine(source, "manifest.json");
using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var root = manifest.RootElement;
if (root.GetProperty("manifestVersion").GetInt32() != 3)
    throw new InvalidDataException("Only v3 manifests can be packed.");
var entryAssembly = root.GetProperty("entryAssembly").GetString()
    ?? throw new InvalidDataException("entryAssembly is required.");
var settingsSchema = root.GetProperty("settingsSchema").GetString()
    ?? throw new InvalidDataException("settingsSchema is required.");
foreach (var required in new[] { entryAssembly, settingsSchema })
{
    if (!File.Exists(Path.Combine(source, required)))
        throw new InvalidDataException($"Required payload '{required}' is missing.");
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.EndsWith("FolderRewind.Plugin.Abstractions.dll", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                continue;
            archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        }
    }
    File.Move(temporary, output, overwrite: true);
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}

await using var package = File.OpenRead(output);
var hash = Convert.ToHexString(await SHA256.HashDataAsync(package)).ToLowerInvariant();
await File.WriteAllTextAsync(output + ".sha256", hash + "  " + Path.GetFileName(output) + Environment.NewLine);
Console.WriteLine($"{output}\nSHA256={hash}");
return 0;
