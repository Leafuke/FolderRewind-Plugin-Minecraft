using FolderRewind.Plugin.Abstractions;

namespace MineRewind;

/// <summary>Minecraft declares dependencies; the Host alone interprets historical filtering semantics.</summary>
internal static class MinecraftMergeCoverage
{
    internal sealed record Issue(string Side, string Component, MergeCoverageState State, string Code, string? Rule, string? Example);
    internal static async Task<IReadOnlyList<Issue>> CheckAsync(MergeAnalysisRequest request, string world, CancellationToken token)
    {
        var root = world == "." ? "" : world;
        var issues = new List<Issue>();
        var inputs = new[] { request.Base, request.Ours, request.Theirs };
        for (var side = 0; side < inputs.Length; side++)
        {
            var boundary = inputs[side].Boundary;
            var rules = boundary.TypedFilterRules.Count > 0 ? boundary.TypedFilterRules : boundary.FilterRules.Select(PathFilterRule.Legacy).ToArray();
            var ignored = rules.Where(MinecraftWorldFilePolicy.IsSemanticNonDependency).ToArray();
            foreach (var component in new[] { "region", "entities", "poi" })
            {
                // All possible dimensions and external payloads participate, including files absent
                // from every visible inventory. Entity migration therefore cannot escape the proof.
                var patterns = new[] { component, "DIM-1/" + component, "DIM1/" + component, "dimensions/*/**/" + component }
                    .SelectMany(prefix => new[] { prefix + "/*.mca", prefix + "/*.mcc" }).ToArray();
                var proof = await inputs[side].Files.CheckCoverageAsync(new(component, root, patterns)
                { IgnoredBlacklistRules = ignored }, token).ConfigureAwait(false);
                var state = proof.BoundaryFingerprint == inputs[side].Boundary.Fingerprint ? proof.State : MergeCoverageState.Unknown;
                if (state == MergeCoverageState.Complete) continue;
                var evidence = proof.Evidence.FirstOrDefault();
                issues.Add(new(((MergeInputSide)side).ToString(), component, state, evidence?.Code ?? "coverage_unavailable",
                    Short(evidence?.Rule), Short(evidence?.ExamplePath)));
            }
        }
        return issues;
    }
    private static string? Short(string? value) => value?.Length > 512 ? value[..512] + "…" : value;
    internal static LocalizedText Describe(IReadOnlyList<Issue> issues)
    {
        string Detail(Issue issue, bool chinese)
        {
            var outcome = issue.State == MergeCoverageState.Filtered ? (chinese ? "历史过滤影响此依赖" : "historical filtering overlaps this dependency")
                : (chinese ? "Host 无法证明此依赖完整覆盖" : "the Host cannot prove complete coverage of this dependency");
            return $"{issue.Side} · {issue.Component}: {outcome}" + (issue.Rule is null ? "" : $" [{issue.Rule}]");
        }
        var en = string.Join("\n", issues.Select(i => Detail(i, false)))
            + "\nThis world cannot be composed safely. Use historical versions with sufficient coverage, or explicitly start a file-merge session. Changing current filters does not repair missing historical data.";
        var zh = string.Join("\n", issues.Select(i => Detail(i, true)))
            + "\n此世界暂不能安全组合。请选择覆盖充分的历史版本，或显式另建文件合并任务。修改当前过滤器不会补回旧历史中未捕获的数据。";
        return new(en, new Dictionary<string, string> { ["en-US"] = en, ["zh-CN"] = zh });
    }
}
