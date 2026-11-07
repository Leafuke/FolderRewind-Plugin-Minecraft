using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static MineRewind.Pack.ProductIdentity;

if (args.Length != 8 || args[2] != "--sdk" || args[4] != "--sdk-package" || args[6] != "--allow-development")
{
    Console.Error.WriteLine("Usage: MineRewind.Pack <staging> <output.frplugin> --sdk <resolved.dll> --sdk-package <exact.nupkg> --allow-development <true|false>");
    return 2;
}

var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
var manifestPath = Path.Combine(source, "manifest.json");
using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
var root = manifest.RootElement;
if (args[7] is not ("true" or "false")) throw new ArgumentException("Invalid development contract switch.");
if (root.GetProperty("manifestVersion").GetInt32() != 3)
    throw new InvalidDataException("Only v3 manifests can be packed.");
var entryAssembly = root.GetProperty("entryAssembly").GetString()
    ?? throw new InvalidDataException("entryAssembly is required.");
var settingsSchema = root.GetProperty("settingsSchema").GetString()
    ?? throw new InvalidDataException("settingsSchema is required.");
if (entryAssembly != "MineRewind.dll" || settingsSchema != "settings.schema.json") throw new InvalidDataException("Unexpected entry or settings path.");
foreach (var required in new[] { entryAssembly, settingsSchema })
{
    if (!File.Exists(Path.Combine(source, required)))
        throw new InvalidDataException($"Required payload '{required}' is missing.");
}

var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{ "MineRewind.dll", "MineRewind.World.dll", "fNbt.dll", "K4os.Compression.LZ4.dll", "System.IO.Hashing.dll", "preview-dependencies.NOTICE.txt", "manifest.json", "settings.schema.json", "contract-source.json" };
var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal).ToArray();
if (files.Length != allowed.Count || !files.All(file => allowed.Contains(Path.GetRelativePath(source, file))))
    throw new InvalidDataException("Package staging inventory differs from the approved payload; use a clean staging directory.");
ValidatePackage(source, root, Path.GetFullPath(args[3]), Path.GetFullPath(args[5]), args[7] == "true");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
    {
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
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

static void ValidatePackage(string source, JsonElement manifest, string sdk, string sdkPackage, bool allowDevelopment)
{
    var product = manifest.GetProperty("version").GetString() ?? throw new InvalidDataException("Missing product version.");
    var numeric = NumericVersion(product);
    var entry = Path.Combine(source, "MineRewind.dll");
    var assembly = AssemblyName.GetAssemblyName(entry);
    var info = FileVersionInfo.GetVersionInfo(entry);
    if (assembly.Name != "MineRewind" || assembly.Version != numeric || info.FileVersion != numeric.ToString()
        || !ProductMatches(info.ProductVersion, product)) throw new InvalidDataException("Entry assembly version differs from Manifest product identity.");
    using (var input = File.OpenRead(entry))
    using (var pe = new PEReader(input))
    {
        var architectures = manifest.GetProperty("architectures").EnumerateArray().Select(a => a.GetString()).ToArray();
        if (architectures.Length != 1 || architectures[0] != "any" || pe.PEHeaders.CoffHeader.Machine != Machine.I386
            || pe.PEHeaders.CorHeader is not { } cor || (cor.Flags & CorFlags.ILOnly) == 0
            || (cor.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit)) != 0)
            throw new InvalidDataException("The entry assembly must satisfy the declared portable architecture.");
        var metadata = pe.GetMetadataReader();
        var entryType = manifest.GetProperty("entryType").GetString();
        if (!metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Any(t =>
            metadata.GetString(t.Namespace) + "." + metadata.GetString(t.Name) == entryType)) throw new InvalidDataException("Entry type is missing.");
        if (!metadata.AssemblyReferences.Select(metadata.GetAssemblyReference).Any(a =>
            metadata.GetString(a.Name) == "FolderRewind.Plugin.Abstractions" && a.Version == new Version(3, 0, 0, 0)))
            throw new InvalidDataException("Entry SDK assembly identity differs.");
    }
    using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "contract-source.json")));
    var contract = provenance.RootElement;
    string Field(string name) => contract.GetProperty(name).GetString() ?? throw new InvalidDataException("Incomplete provenance.");
    var api = manifest.GetProperty("pluginApi");
    var apiVersion = $"{api.GetProperty("major").GetInt32()}.{api.GetProperty("minor").GetInt32()}";
    var sdkVersion = apiVersion + ".0";
    if (apiVersion != "3.18" || Field("apiVersion") != apiVersion || Field("version") != sdkVersion
        || Field("packageId") != "FolderRewind.Plugin.Abstractions" || Field("assemblyVersion") != "3.0.0.0"
        || !Regex.IsMatch(Field("sourceCommit"), "^[0-9a-fA-F]{40}$") || !Regex.IsMatch(Field("sourceDigestSha256"), "^[0-9a-fA-F]{64}$")
        || contract.GetProperty("sources").GetArrayLength() == 0) throw new InvalidDataException("SDK API or provenance differs.");
    var packageSource = Field("packageSource");
    if (Field("sourceState") == "development-candidate")
    {
        if (!allowDevelopment || !Path.IsPathFullyQualified(packageSource) || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(packageSource), sdkPackage))
            throw new InvalidDataException("A local candidate requires explicit development packaging and its exact isolated package.");
    }
    else if (Field("sourceState") != "committed" || packageSource != $"https://api.nuget.org/v3-flatcontainer/folderrewind.plugin.abstractions/{sdkVersion}/folderrewind.plugin.abstractions.{sdkVersion}.nupkg")
        throw new InvalidDataException("Formal packaging requires frozen public SDK provenance.");
    using (var package = File.OpenRead(sdkPackage))
        if (!Convert.ToHexString(SHA256.HashData(package)).Equals(Field("packageSha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SDK package hash differs.");
    using (var zip = ZipFile.OpenRead(sdkPackage))
    {
        using var dll = (zip.GetEntry("lib/net10.0/FolderRewind.Plugin.Abstractions.dll") ?? throw new InvalidDataException("SDK assembly missing.")).Open();
        if (!Convert.ToHexString(SHA256.HashData(dll)).Equals(Field("assemblySha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Packaged SDK assembly hash differs.");
        using var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
        var metadata = XDocument.Load(nuspec).Descendants().Single(e => e.Name.LocalName == "metadata");
        if (metadata.Elements().Single(e => e.Name.LocalName == "id").Value != Field("packageId")
            || metadata.Elements().Single(e => e.Name.LocalName == "version").Value != sdkVersion) throw new InvalidDataException("SDK package identity differs.");
    }
    using (var input = File.OpenRead(sdk))
        if (!Convert.ToHexString(SHA256.HashData(input)).Equals(Field("assemblySha256"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Actual SDK assembly hash differs.");
    var sdkInfo = FileVersionInfo.GetVersionInfo(sdk);
    var sdkIdentity = AssemblyName.GetAssemblyName(sdk);
    if (sdkIdentity.Name != "FolderRewind.Plugin.Abstractions" || sdkIdentity.Version != new Version(3, 0, 0, 0) || sdkInfo.FileVersion != NumericVersion(sdkVersion).ToString()
        || !ProductMatches(sdkInfo.ProductVersion, sdkVersion)) throw new InvalidDataException("Actual SDK version differs.");
    using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "settings.schema.json")));
}
