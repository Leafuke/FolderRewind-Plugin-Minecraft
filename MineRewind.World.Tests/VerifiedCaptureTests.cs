using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

namespace MineRewind.World.Tests;

public sealed partial class SemanticTests
{
    [TestMethod]
    public void FullPayloadIoSeparatesStrictStreamingAndProtectedCapture()
    {
        var source = Dir("input"); const int size = 4 * 1024 * 1024;
        File.WriteAllBytes(Path.Combine(source, "level.dat"), new byte[size]);
        foreach (var verified in new[] { false, true })
        {
            var fixedSource = new FixedDigestSource(new DirectoryWorldSource(source));
            var countedSource = new IoSource(fixedSource);
            var scratch = new IoScratch(new DirectorySemanticScratch(Path.Combine(_root, "io-" + verified), true));
            if (verified) WorldCapture.CaptureVerified(countedSource, scratch, "source-1");
            else WorldCapture.Capture(countedSource, scratch, "source-1");
            long stagingBytes = 0;
            foreach (var path in WorldFullFormat.PrepareExport(scratch, default))
            {
                using var input = scratch.OpenRead(path);
                if (path.EndsWith(".bin", StringComparison.Ordinal)) stagingBytes += input.Length;
                input.CopyTo(Stream.Null);
            }
            Assert.AreEqual((verified ? 1L : 4L) * size, countedSource.ReadBytes);
            Assert.AreEqual(2L * size, scratch.ReadBytes, "Independent reconstruction validation and staging export both remain.");
            Assert.AreEqual(size, scratch.WriteBytes);
            Assert.AreEqual(size, stagingBytes);
            Console.WriteLine($"verified={verified}; source={countedSource.ReadBytes}; scratch-read={scratch.ReadBytes}; scratch-write={scratch.WriteBytes}; staging-write={stagingBytes}; metadata excluded");
        }
    }

    private sealed class IoSource(IReadOnlyWorldSource inner) : IReadOnlyWorldSource
    {
        internal long ReadBytes;
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => inner.GetInventory(token);
        public Stream OpenRead(string path) => new IoStream(inner.OpenRead(path), count => ReadBytes += count, _ => { });
    }
    private sealed class IoScratch(ISemanticScratch inner) : ISemanticScratch
    {
        internal long ReadBytes, WriteBytes;
        public bool Exists(string path) => inner.Exists(path);
        public Stream OpenRead(string path) => new IoStream(inner.OpenRead(path), count => { if (path.EndsWith(".bin", StringComparison.Ordinal)) ReadBytes += count; }, _ => { });
        public Stream Create(string path) => new IoStream(inner.Create(path), _ => { }, count => { if (path.EndsWith(".bin", StringComparison.Ordinal)) WriteBytes += count; });
    }
    private sealed class IoStream(Stream inner, Action<int> read, Action<int> write) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); read(n); return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); read(n); return n; }
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); write(count); }
        public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); write(buffer.Length); }
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [TestMethod]
    [DataRow(1, false)] [DataRow(2, false)] [DataRow(3, false)]
    [DataRow(1, true)] [DataRow(2, true)] [DataRow(3, true)] [DataRow(99, false)]
    public void VerifiedAndStrictCaptureKeepIdenticalPersistentFiles(int compression, bool external)
    {
        var source = Dir("input"); Region(source, compression: compression, external: external);
        File.WriteAllText(Path.Combine(source, "level.dat"), "literal metadata");
        File.WriteAllBytes(Path.Combine(source, "empty"), []);
        var strict = Capture(source);
        var verified = new DirectorySemanticScratch(Path.Combine(_root, "verified"), true);
        WorldCapture.CaptureVerified(new FixedDigestSource(new DirectoryWorldSource(source)), verified, "source-1");
        var strictFiles = WorldFullFormat.PrepareExport(strict, default);
        var verifiedFiles = WorldFullFormat.PrepareExport(verified, default);
        CollectionAssert.AreEqual(strictFiles.ToArray(), verifiedFiles.ToArray());
        foreach (var path in strictFiles)
        {
            using var left = strict.OpenRead(path); using var right = verified.OpenRead(path);
            CollectionAssert.AreEqual(SHA256.HashData(left), SHA256.HashData(right), path);
        }
        CheckRoundTrip(source, verified);
    }

    [TestMethod]
    [DataRow("same-size")] [DataRow("truncate")] [DataRow("external")]
    public void VerifiedCaptureRejectsBytesChangedAfterTheFixedInventory(string change)
    {
        var source = Dir("input"); Region(source, external: true);
        var path = Path.Combine(source, "level.dat"); File.WriteAllBytes(path, [1, 2, 3]);
        var fixedSource = new FixedDigestSource(new DirectoryWorldSource(source));
        if (change == "external") File.WriteAllBytes(Directory.GetFiles(source, "*.mcc", SearchOption.AllDirectories).Single(), Nbt(9));
        else File.WriteAllBytes(path, change == "truncate" ? [1] : [3, 2, 1]);
        var scratch = new DirectorySemanticScratch(Path.Combine(_root, "failed"), true);
        Assert.ThrowsExactly<IOException>(() => WorldCapture.CaptureVerified(fixedSource, scratch, "source-1"));
        Assert.IsFalse(scratch.Exists("complete.json"));
    }

    [TestMethod]
    public void VerifiedCaptureCancellationAndScratchFailureNeverPublishCompletion()
    {
        var source = Dir("input"); File.WriteAllBytes(Path.Combine(source, "level.dat"), new byte[65536]);
        var fixedSource = new FixedDigestSource(new DirectoryWorldSource(source));
        var scratch = new DirectorySemanticScratch(Path.Combine(_root, "cancelled"), true);
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => WorldCapture.CaptureVerified(fixedSource,
            new CancelScratch(scratch, cancel), "source-1", cancel.Token));
        Assert.IsFalse(scratch.Exists("complete.json"));
        var faulted = new DirectorySemanticScratch(Path.Combine(_root, "faulted"), true);
        Assert.ThrowsExactly<IOException>(() => WorldCapture.CaptureVerified(fixedSource, new FaultScratch(faulted), "source-1"));
        Assert.IsFalse(faulted.Exists("complete.json"));
    }

    private sealed class FixedDigestSource : IReadOnlyWorldSource
    {
        private readonly IReadOnlyWorldSource _inner;
        private readonly SourceFile[] _files;
        public FixedDigestSource(IReadOnlyWorldSource inner)
        {
            _inner = inner;
            _files = inner.GetInventory(default).Select(file =>
            {
                using var stream = inner.OpenRead(file.RelativePath);
                return file with { Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
            }).ToArray();
        }
        public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) { token.ThrowIfCancellationRequested(); return _files; }
        public Stream OpenRead(string path) => _inner.OpenRead(path);
    }
}
