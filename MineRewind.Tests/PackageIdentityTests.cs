using MineRewind.Pack;

namespace MineRewind.Tests;

[TestClass]
public sealed class PackageIdentityTests
{
    [TestMethod]
    [DataRow("1.10.2", "1.10.2", true)]
    [DataRow("1.10.2+abcdef0123456789", "1.10.2", true)]
    [DataRow("1.10.2-rc.1+abcdef0123456789", "1.10.2-rc.1", true)]
    [DataRow("1.10.2-rc.1+build.abcdef0123456789", "1.10.2-rc.1+build", true)]
    [DataRow("1.10.2-rc.2+abcdef0123456789", "1.10.2-rc.1", false)]
    [DataRow("1.10.3+abcdef0123456789", "1.10.2", false)]
    [DataRow("1.10.2+unexpected-label", "1.10.2", false)]
    public void ProductIdentityPreservesPrereleaseAndOnlyAcceptsSdkGitSuffix(string actual, string expected, bool matches)
    {
        Assert.AreEqual(matches, ProductIdentity.ProductMatches(actual, expected));
        Assert.AreEqual(new Version(1, 10, 2, 0), ProductIdentity.NumericVersion(expected));
    }
}
