using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class WindowsReleaseTableTests
{
    [TestMethod]
    [DataRow("10.0.10240", "Windows10_1507")]
    [DataRow("10.0.10586", "Windows10_1511")]
    [DataRow("10.0.14393", "Windows10_1607")]
    [DataRow("10.0.15063", "Windows10_1703")]
    [DataRow("10.0.16299", "Windows10_1709")]
    [DataRow("10.0.17134", "Windows10_1803")]
    [DataRow("10.0.17763", "Windows10_1809")]
    [DataRow("10.0.18362", "Windows10_1903")]
    [DataRow("10.0.18363", "Windows10_1909")]
    [DataRow("10.0.19041", "Windows10_2004")]
    [DataRow("10.0.19042", "Windows10_20H2")]
    [DataRow("10.0.19043", "Windows10_21H1")]
    [DataRow("10.0.19044", "Windows10_21H2")]
    [DataRow("10.0.19045", "Windows10_22H2")]
    [DataRow("10.0.22000", "Windows11_21H2")]
    [DataRow("10.0.22621", "Windows11_22H2")]
    [DataRow("10.0.22631", "Windows11_23H2")]
    [DataRow("10.0.26100", "Windows11_24H2")]
    public void Map_KnownBuild_ReturnsExpectedRelease(string minimumOsVersion, string expectedRelease)
    {
        var release = WindowsReleaseTable.Map(minimumOsVersion);

        Assert.AreEqual(expectedRelease, release);
    }

    [TestMethod]
    [DataRow(" 10.0.19045 ")]
    [DataRow("\t10.0.19045")]
    public void Map_BuildWithSurroundingWhitespace_IsTrimmedBeforeLookup(string minimumOsVersion)
    {
        var release = WindowsReleaseTable.Map(minimumOsVersion);

        Assert.AreEqual("Windows10_22H2", release);
    }

    [TestMethod]
    [DataRow("10.0.99999")]
    [DataRow("")]
    [DataRow("not-a-build")]
    public void Map_UnknownBuild_Throws(string minimumOsVersion)
    {
        var exception = Assert.ThrowsExactly<UnsupportedWindowsBuildException>(
            () => WindowsReleaseTable.Map(minimumOsVersion));

        Assert.AreEqual(minimumOsVersion, exception.MinimumOsVersion);
    }
}
