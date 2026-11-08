namespace MineRewind.World.Semantic;

public static class WorldVerification
{
    public static VerificationResult VerifySource(IReadOnlyWorldSource source, ISemanticScratch scratch, CancellationToken token = default)
    {
        var inventory = source.GetInventory(token).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var errors = new List<string>(); int count = 0; long bytes = 0;
        foreach (var file in ScratchArchive.ReadLines<StoredFile>(scratch, "files.jsonl", token))
        {
            token.ThrowIfCancellationRequested(); count++; bytes += file.Length;
            if (!inventory.Remove(file.RelativePath, out var actual)) { errors.Add("missing:" + file.RelativePath); continue; }
            using var input = source.OpenRead(file.RelativePath);
            if (actual.Length != file.Length || SemanticStreams.Hash(input, token) != file.Sha256) errors.Add("bytes:" + file.RelativePath);
        }
        errors.AddRange(inventory.Keys.Select(p => "extra:" + p));
        return new(errors.Count == 0, count, bytes, errors);
    }
}
