namespace MineRewind.World.Semantic;

/// <summary>One coordinate's three terrain inputs. Never retains decoded bytes or NBT objects.</summary>
internal sealed class MergeChunkFingerprintCache
{
    internal const int MaximumEntries = 3 * 1024;
    private readonly Dictionary<(ChunkKey Chunk, string PayloadHash, string Algorithm), string?> _identities = [];
    internal int Count => _identities.Count;

    internal string? ReadValidated(byte[] bytes, int length, ChunkKey chunk, string payloadHash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var key = (chunk, payloadHash, ChunkContentFingerprint.MergeAlgorithmVersion);
        if (_identities.TryGetValue(key, out var identity)) return identity;
        identity = ChunkContentFingerprint.ReadValidated(bytes, length, chunk, token)?.MergeIdentity;
        token.ThrowIfCancellationRequested();
        // Cache null as well: unsupported-but-valid payloads retain the raw identity fallback.
        if (_identities.Count < MaximumEntries) _identities.Add(key, identity);
        return identity;
    }
}
