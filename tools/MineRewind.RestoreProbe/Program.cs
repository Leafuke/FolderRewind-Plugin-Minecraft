using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using fNbt;
using FolderRewind.Plugin.Abstractions;
using MineRewind;

// Opt-in diagnostics only: finite synthetic samples, each parsed in a disposable process.
// The observations neither impose parser limits nor establish safety for arbitrary NBT.
if (args is ["--child", var childSample])
{
    var input = Sample(childSample);
    var target = Sample("normal");
    var allocated = GC.GetTotalAllocatedBytes(true);
    var watch = Stopwatch.StartNew();
    string outcome;
    try
    {
        var files = await NbtHelper.PreparePlayerDataAsync(new View(input), new View(target), CancellationToken.None);
        outcome = "accepted:" + files.Count;
    }
    catch (Exception error) { outcome = error.GetType().Name + ":" + error.Message; }
    Console.WriteLine(JsonSerializer.Serialize(new { sample = childSample, compressedBytes = input.Length, outcome,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, elapsedMs = watch.ElapsedMilliseconds,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
    return;
}
if (args is not ["--run"]) throw new ArgumentException("Explicitly enable with --run; output is diagnostic JSON, not a stable test gate.");
var results = new List<object>();
foreach (var sample in new[] { "normal", "compressed-array-8m", "nested-128", "truncated-array-8m" })
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(View).Assembly.Location); start.ArgumentList.Add("--child"); start.ArgumentList.Add(sample);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var timedOut = false;
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { timedOut = true; process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
    results.Add(new { sample, timedOut, exitCode = process.ExitCode, stdout = await stdout, stderr = await stderr });
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

static byte[] Sample(string name)
{
    var data = new NbtCompound("Data") { new NbtInt("DataVersion", 4321),
        new NbtCompound("Player") { new NbtInt("XpLevel", 7) } };
    switch (name)
    {
        case "normal": break;
        case "compressed-array-8m": data.Add(new NbtByteArray("unrelated", new byte[8 * 1024 * 1024])); break;
        case "nested-128":
            var cursor = data;
            for (var i = 0; i < 128; i++) { var nested = new NbtCompound("nested"); cursor.Add(nested); cursor = nested; }
            break;
        case "truncated-array-8m":
            // Root compound, empty name, byte-array tag x with a finite 8 MiB length and no payload.
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
                    gzip.Write(new byte[] { 10, 0, 0, 7, 0, 1, (byte)'x', 0, 128, 0, 0 });
                return output.ToArray();
            }
        default: throw new ArgumentException("Unknown bounded sample.");
    }
    return new NbtFile(new NbtCompound("") { data }).SaveToBuffer(NbtCompression.GZip);
}

sealed class View(byte[] bytes) : IRestoreSourceView
{
    public IReadOnlyList<string> RelativePaths => ["level.dat"];
    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); if (path != "level.dat") throw new FileNotFoundException(); return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false)); }
}
