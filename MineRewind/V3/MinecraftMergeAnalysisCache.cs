using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FolderRewind.Plugin.Abstractions;
using MineRewind.World.Semantic;

namespace MineRewind;

/// <summary>Successful metadata only, scoped to one world/dimension/region coordinate in Analyze.</summary>
internal sealed class MinecraftMergeAnalysisCache
{
    internal const int MaximumRegions = 9;
    private sealed record RegionKey(string Path, RegionAddress Address, string Boundary, long Length, string Digest, string ExternalFamily);
    private readonly Dictionary<RegionKey, MergeRegionIndex> _regions = [];
    private readonly MergeChunkFingerprintCache _fingerprints = new();
    internal int RegionCount => _regions.Count;
    internal int FingerprintCount => _fingerprints.Count;

    internal async Task<MergeRegionIndex> ReadAsync(string path, RegionAddress address, MergeVersionInput input,
        IReadOnlyDictionary<string, MergeInputEntry> inventory, IReadOnlySet<string> paths,
        IReadOnlyList<string> possibleExternalFamily, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = inventory[path];
        var family = possibleExternalFamily.Where(inventory.ContainsKey).Select(p => inventory[p]).ToArray();
        // Unknown identities cannot establish equivalence. The uncached reader still classifies them.
        var key = string.IsNullOrEmpty(input.Boundary.Fingerprint) || string.IsNullOrEmpty(entry.Digest)
            || family.Any(f => string.IsNullOrEmpty(f.Digest)) ? null
            : new RegionKey(path, address, input.Boundary.Fingerprint, entry.Length, entry.Digest, FamilyIdentity(family));
        if (key is not null && _regions.TryGetValue(key, out var cached))
        {
            // Open every file the original reader would open on this side. The Host verifies its
            // fixed file identity before returning the stream; sharing metadata must not bypass it.
            await VerifyOpenAsync(path, region: true).ConfigureAwait(false);
            foreach (var external in cached.Chunks.Select(c => c.ExternalPath).OfType<string>())
                await VerifyOpenAsync(external).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return cached;
        }
        var index = await MergeRegion.ReadForAnalysisAsync(path, address, input.Files.OpenReadAsync, paths, token, _fingerprints).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (key is not null && _regions.Count < MaximumRegions) _regions.Add(key, index);
        return index;

        async Task VerifyOpenAsync(string relative, bool region = false)
        {
            token.ThrowIfCancellationRequested();
            await using var stream = await input.Files.OpenReadAsync(relative, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (region && !stream.CanSeek) throw new InvalidDataException("merge_invalid_region_header: " + relative);
            if (stream.CanSeek && stream.Length != inventory[relative].Length)
                throw new InvalidDataException("merge_input_length_changed: " + relative);
        }
    }

    private static string FamilyIdentity(IEnumerable<MergeInputEntry> family)
    {
        // Length-delimited fields and ordinal path order distinguish absence, aliases and all
        // unreferenced .mcc siblings too. This is an internal cache key, never a persisted identity.
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true))
            foreach (var item in family.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            { writer.Write(item.RelativePath); writer.Write(item.Length); writer.Write(item.Digest); }
        return Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length))));
    }

    internal static Dictionary<(string Parent, int X, int Z), string[]> IndexExternalFamilies(IEnumerable<string> paths)
    {
        var families = new Dictionary<(string Parent, int X, int Z), List<string>>();
        foreach (var path in paths)
        {
            var slash = path.LastIndexOf('/') + 1;
            var parts = path[slash..].Split('.');
            if (parts.Length != 4 || parts[0] != "c" || parts[3] != "mcc"
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z)) continue;
            var key = (path[..slash], (int)Math.Floor(x / 32d), (int)Math.Floor(z / 32d));
            if (!families.TryGetValue(key, out var family)) families.Add(key, family = []);
            family.Add(path);
        }
        return families.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray());
    }
}
