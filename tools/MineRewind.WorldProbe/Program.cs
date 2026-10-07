using System.Diagnostics;
using System.Text.Json;
using MineRewind.World.Semantic;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("WorldProbe index|roundtrip --source <offline-world> --source-id <id> --output <new-directory>\n"
            + "WorldProbe compare --base <offline-world> --ours <offline-world> --theirs <offline-world> --source-id <id> --output <new-directory>\n"
            + "Exit 0: verification passed; 1: validation/analysis needs attention; 2: execution error. Never applies a merge.");
        return 0;
    }
    var command = args[0];
    if (command is not ("index" or "roundtrip" or "compare")) throw new ArgumentException("Unknown command.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Expected unique --option value pairs.");
    }
    string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("Missing " + key);
    var sourceId = Required("--source-id"); var output = Path.GetFullPath(Required("--output"));
    var keys = command == "compare" ? new[] { "--base", "--ours", "--theirs" } : ["--source"];
    if (options.Keys.Except(keys.Concat(["--source-id", "--output"])).Any()) throw new ArgumentException("Unknown option.");
    var inputs = keys.Select(k => Path.GetFullPath(Required(k))).ToArray();
    SemanticPaths.RequireDisjoint(output, inputs);
    // Validate every source before allocating output; never skip an unreadable input.
    var io = new IoCounters();
    var sources = inputs.Select(p => new CountedSource(new DirectoryWorldSource(p), n => io.SourceReadBytes += n)).ToArray();
    foreach (var source in sources) source.GetInventory(cancellation.Token);
    var run = new DirectorySemanticScratch(output, create: true);
    var timer = Stopwatch.StartNew();
    var snapshots = new List<ISemanticScratch>();
    var completions = new List<ScratchCompletion>();
    for (var i = 0; i < sources.Length; i++)
    {
        var snapshot = new CountedScratch(new DirectorySemanticScratch(Path.Combine(output, "snapshot-" + i), create: true),
            n => io.ScratchReadBytes += n, n => io.ScratchWriteBytes += n);
        completions.Add(WorldCapture.Capture(sources[i], snapshot, sourceId, cancellation.Token));
        snapshots.Add(snapshot);
    }
    VerificationResult? verification = null; ComparisonSummary? comparison = null;
    if (command == "roundtrip")
    {
        var restored = new DirectorySemanticScratch(Path.Combine(output, "reconstructed"), create: true);
        ScratchArchive.Reconstruct(snapshots[0], new CountedScratch(restored, _ => { }, n => io.ReconstructedWriteBytes += n), cancellation.Token);
        verification = ScratchArchive.VerifySource(new CountedSource(new DirectoryWorldSource(restored.Root),
            n => io.ReconstructionVerificationReadBytes += n), snapshots[0], cancellation.Token);
    }
    else if (command == "compare")
    {
        using var bundles = new StreamWriter(run.Create("bundles.jsonl"));
        using var files = new StreamWriter(run.Create("file-changes.jsonl"));
        comparison = WorldComparison.Compare(snapshots[0], snapshots[1], snapshots[2],
            d => bundles.WriteLine(JsonSerializer.Serialize(d, SemanticJson.Options)),
            d => files.WriteLine(JsonSerializer.Serialize(d, SemanticJson.Options)), cancellation.Token);
    }
    var success = completions.All(c => c.Summary.InputComplete && c.Summary.FullyComparable)
        && verification?.Success != false && (comparison is null || comparison.Conflicts + comparison.Blocked + comparison.DimensionReviews == 0);
    var report = new
    {
        Command = command, SourceId = sourceId, Experimental = true, ApplicableMerge = false,
        VerificationPassed = success, Captures = completions.Select(c => c.Summary), Verification = verification, Comparison = comparison,
        ElapsedSeconds = timer.Elapsed.TotalSeconds, PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        AllocatedBytes = GC.GetTotalAllocatedBytes(), ManagedHeapBytes = GC.GetTotalMemory(false), Io = io
    };
    cancellation.Token.ThrowIfCancellationRequested();
    using (var stream = run.Create("result.json")) JsonSerializer.Serialize(stream, report, new JsonSerializerOptions(SemanticJson.Options) { WriteIndented = true });
    Console.WriteLine(JsonSerializer.Serialize(report, SemanticJson.Options));
    return success ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Canceled; incomplete outputs are not valid results."); return 2; }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
{ Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 2; }
