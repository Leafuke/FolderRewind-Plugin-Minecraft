using MineRewind.World.Semantic;

namespace MineRewind;

/// <summary>The reservation is an upper bound, enforced before each write, not an estimate silently exceeded.</summary>
internal sealed class BudgetedSemanticScratch(ISemanticScratch inner, long maximumBytes) : ISemanticScratch
{
    private long _written;
    internal long WrittenBytes => Interlocked.Read(ref _written);
    public Stream OpenRead(string path) => inner.OpenRead(path);
    public bool Exists(string path) => inner.Exists(path);
    public Stream Create(string path) => new BudgetedWrite(inner.Create(path), Reserve);
    private void Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var total = Interlocked.Add(ref _written, count);
        if (total <= maximumBytes) return;
        Interlocked.Add(ref _written, -count);
        throw new IOException("Semantic scratch exceeded its reserved byte budget.");
    }
    private sealed class BudgetedWrite(Stream stream, Action<int> reserve) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => stream.CanWrite;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { reserve(count); stream.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { reserve(buffer.Length); stream.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); reserve(buffer.Length); await stream.WriteAsync(buffer, token).ConfigureAwait(false); }
        public override void Flush() => stream.Flush();
        public override Task FlushAsync(CancellationToken token) => stream.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        { await stream.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
    }
}
