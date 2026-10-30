using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MineRewind.World.Semantic;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class VerifiedObjectStreamTests
{
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    [TestMethod]
    public void SequentialReadVerifiesBeforeReturningTheLastBytes()
    {
        using var good = new VerifiedObjectStream(new MemoryStream([1, 2, 3]), 3, Hash([1, 2, 3]), default);
        Assert.AreEqual(1, good.ReadByte());
        var buffer = new byte[8]; Assert.AreEqual(2, good.Read(buffer)); Assert.AreEqual(0, good.Read(buffer));
        using var bad = new VerifiedObjectStream(new MemoryStream([1, 2, 4]), 3, Hash([1, 2, 3]), default);
        Assert.AreEqual(1, bad.ReadByte());
        Assert.ThrowsExactly<InvalidDataException>(() => bad.Read(buffer));
        Assert.ThrowsExactly<InvalidDataException>(() => bad.Read(buffer));
    }
    [TestMethod]
    public void TruncationCancellationAndSeekingCannotBypassVerification()
    {
        using var truncated = new VerifiedObjectStream(new MemoryStream([1]), 2, Hash([1, 2]), default);
        Assert.ThrowsExactly<EndOfStreamException>(() => truncated.CopyTo(Stream.Null));
        using var canceled = new VerifiedObjectStream(new MemoryStream([1]), 1, Hash([1]), new CancellationToken(true));
        Assert.ThrowsExactly<OperationCanceledException>(() => canceled.ReadByte());
        Assert.ThrowsExactly<NotSupportedException>(() => canceled.Position = 1);
        using var empty = new VerifiedObjectStream(new MemoryStream(), 0, Hash([1]), default);
        Assert.ThrowsExactly<InvalidDataException>(() => empty.ReadByte());
    }
    [TestMethod]
    public void PartialDisposalDoesNotReadTheRemainderOrCertifyIt()
    {
        var input = new TrackingStream([1, 2, 3]);
        using (var stream = new VerifiedObjectStream(input, 3, Hash([9]), default)) Assert.AreEqual(1, stream.ReadByte());
        Assert.AreEqual(1, input.BytesRead); Assert.IsTrue(input.Closed);
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead; public bool Closed;
        public override int Read(Span<byte> buffer) { var count = base.Read(buffer); BytesRead += count; return count; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
}
