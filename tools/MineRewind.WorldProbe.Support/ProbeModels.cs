namespace MineRewind.World.Semantic;

public sealed record ProbeDiagnostic(string RelativePath, string Code);
public sealed record VerificationResult(bool Success, int Files, long Bytes, IReadOnlyList<string> Errors);
