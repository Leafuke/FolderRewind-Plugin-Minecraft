using MineRewind.World.Semantic;

internal sealed class IoCounters
{
    public long SourceReadBytes { get; set; }
    public long ScratchReadBytes { get; set; }
    public long ScratchWriteBytes { get; set; }
    public long ReconstructedWriteBytes { get; set; }
    public long ReconstructionVerificationReadBytes { get; set; }
}

internal sealed class CountedSource(IReadOnlyWorldSource inner, Action<int> read) : IReadOnlyWorldSource
{
    public IReadOnlyList<SourceFile> GetInventory(CancellationToken token) => inner.GetInventory(token);
    public Stream OpenRead(string path) => new CountedStream(inner.OpenRead(path), read, _ => { });
}

internal sealed class CountedScratch(ISemanticScratch inner, Action<int> read, Action<int> write) : ISemanticScratch
{
    public Stream OpenRead(string path) => new CountedStream(inner.OpenRead(path), read, _ => { });
    public Stream Create(string path) => new CountedStream(inner.Create(path), _ => { }, write);
    public bool Exists(string path) => inner.Exists(path);
}

internal sealed class CountedStream(Stream inner, Action<int> read, Action<int> write) : Stream
{
    public override int Read(byte[] buffer, int offset, int count)
    { var result = inner.Read(buffer, offset, count); read(result); return result; }
    public override int Read(Span<byte> buffer)
    { var result = inner.Read(buffer); read(result); return result; }
    public override void Write(byte[] buffer, int offset, int count)
    { inner.Write(buffer, offset, count); write(count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { inner.Write(buffer); write(buffer.Length); }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
