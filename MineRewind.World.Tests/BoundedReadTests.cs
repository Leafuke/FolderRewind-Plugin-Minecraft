using System.IO.Compression;
using fNbt;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MineRewind.World.Tests;

[TestClass]
public sealed class BoundedReadTests
{
    [TestMethod]
    public void CompressedAndDecodedBudgetsAreIndependentAndCancellationIsNotSwallowed()
    {
        var file = new NbtFile(new NbtCompound("") { new NbtByteArray("payload", new byte[8192]) });
        var encoded = file.SaveToBuffer(NbtCompression.GZip);
        using var tooLarge = new MemoryStream(encoded);
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbtReader.Read(tooLarge, new(1024, 1024), default));
        using var smallInput = new MemoryStream(encoded);
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbtReader.Read(smallInput, new(1, 16384), default));
        using var canceled = new MemoryStream(encoded);
        Assert.ThrowsExactly<OperationCanceledException>(() => BoundedNbtReader.Read(canceled, NbtReadLimits.Display, new(true)));
        using var valid = new MemoryStream(encoded);
        Assert.HasCount(8192, BoundedNbtReader.Read(valid, NbtReadLimits.Display, default).RootTag.Get<NbtByteArray>("payload")!.Value);
    }
    [TestMethod]
    public void StructureIsValidatedEvenWhenProjectionDiscardsTheLargeSubtree()
    {
        var root = new NbtCompound(""); var parent = root;
        for (var i = 0; i < 66; i++) { var next = new NbtCompound("child"); parent.Add(next); parent = next; }
        using var data = new MemoryStream(new NbtFile(root).SaveToBuffer(NbtCompression.None));
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbtReader.Read(data, NbtReadLimits.Display, default, _ => false));
    }
    [TestMethod]
    public void EmptyDirectoriesConsumeTheSameCallBudget()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MineRewind.Budget", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            for (var i = 0; i < 5; i++) Directory.CreateDirectory(Path.Combine(root, i.ToString()));
            var budget = new WorldScanBudget(4);
            Assert.ThrowsExactly<WorldScanLimitException>(() => budget.Files(root, root, default, false).ToArray());
            Assert.IsTrue(budget.Incomplete);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void PreservationProfileAllowsLargeValidPlayersAndObjectProjectionObservesCancellation()
    {
        var root = new NbtCompound("");
        for (var i = 0; i < 17; i++) root.Add(new NbtByteArray("array" + i, new byte[1024 * 1024]));
        var encoded = new NbtFile(root).SaveToBuffer(NbtCompression.GZip);
        using var display = new MemoryStream(encoded);
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedNbtReader.Read(display, NbtReadLimits.Display, default));
        using var preservation = new MemoryStream(encoded);
        Assert.HasCount(17, BoundedNbtReader.Read(preservation, NbtReadLimits.Preservation, default).RootTag);
        using var cancellation = new CancellationTokenSource(); using var projection = new MemoryStream(encoded);
        Assert.ThrowsExactly<OperationCanceledException>(() => BoundedNbtReader.Read(projection, NbtReadLimits.Preservation, cancellation.Token,
            _ => { cancellation.Cancel(); return true; }));
    }
    [TestMethod]
    public void DisplayLocatorBoundsOrdinaryEmptyDirectoriesAndServerProperties()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MineRewind.LocatorBudget", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            for (var i = 0; i < 6; i++) Directory.CreateDirectory(Path.Combine(root, "empty" + i));
            var scan = new WorldScanBudget(3);
            Assert.IsNull(WorldLocator.ResolveForDisplay(root, default, scan)); Assert.IsTrue(scan.Incomplete);
            File.WriteAllText(Path.Combine(root, "server.properties"), new string('x', 65537));
            var properties = new WorldScanBudget();
            Assert.IsNull(WorldLocator.ResolveForDisplay(root, default, properties)); Assert.IsTrue(properties.Incomplete);
            Assert.ThrowsExactly<OperationCanceledException>(() => WorldLocator.ResolveForDisplay(root, new(true), new WorldScanBudget()));
        }
        finally { Directory.Delete(root, true); }
    }
}
