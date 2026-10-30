using System.Security.Cryptography;

namespace MineRewind.World.Semantic;

/// <summary>Verifies while consuming. Callers may only commit after complete successful consumption.</summary>
public sealed class VerifiedObjectStream(Stream input, long length, string digest, CancellationToken token) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _position;
    private bool _verified, _disposed, _faulted;
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidDataException("Object verification previously failed.");
        token.ThrowIfCancellationRequested();
        if (_position == length) { Verify(); return 0; }
        if (buffer.IsEmpty) return 0;
        var count = input.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]);
        if (count == 0) throw new EndOfStreamException("Object ended before its declared length.");
        _hash.AppendData(buffer[..count]); _position += count;
        if (_position == length) Verify();
        return count;
    }
    private void Verify()
    {
        if (_verified) return;
        if (!StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(_hash.GetHashAndReset()), digest))
        { _faulted = true; throw new InvalidDataException("Object reference digest mismatch."); }
        _verified = true;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _hash.Dispose(); input.Dispose(); }
        base.Dispose(disposing);
    }
    public override bool CanRead => !_disposed; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
