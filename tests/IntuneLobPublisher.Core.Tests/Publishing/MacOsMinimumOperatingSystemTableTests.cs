using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class MacOsMinimumOperatingSystemTableTests
{
    [TestMethod]
    [DataRow("10.13")]
    [DataRow("10.14")]
    [DataRow("10.15")]
    [DataRow("11")]
    [DataRow("11.0")]
    [DataRow("12")]
    [DataRow("13")]
    public void Map_KnownV1Version_SetsExactlyOneFlag(string version)
    {
        var payload = MacOsMinimumOperatingSystemTable.Map(version, useBeta: false);

        var flags = new[] { payload.V10_13, payload.V10_14, payload.V10_15, payload.V11_0, payload.V12_0, payload.V13_0, payload.V14_0, payload.V15_0 };
        Assert.AreEqual(1, flags.Count(f => f));
    }

    [TestMethod]
    public void Map_MacOs14WithUseBetaTrue_SetsV14Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("14.0", useBeta: true);

        Assert.IsTrue(payload.V14_0);
        Assert.IsFalse(payload.V13_0);
    }

    [TestMethod]
    public void Map_MacOs15WithUseBetaTrue_SetsV15Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("15", useBeta: true);

        Assert.IsTrue(payload.V15_0);
    }

    [TestMethod]
    [DataRow("14")]
    [DataRow("14.0")]
    [DataRow("15")]
    [DataRow("15.0")]
    public void Map_BetaOnlyVersionWithUseBetaFalse_ThrowsRequiresBetaOnlyFlag(string version)
    {
        var ex = Assert.ThrowsExactly<UnsupportedMacOsVersionException>(
            () => MacOsMinimumOperatingSystemTable.Map(version, useBeta: false));

        Assert.IsTrue(ex.RequiresBetaOnlyFlag);
        StringAssert.Contains(ex.Message, "AppType 'pkg'");
    }

    [TestMethod]
    [DataRow("9.0")]
    [DataRow("16.0")]
    [DataRow("not-a-version")]
    public void Map_UnknownVersion_Throws(string version)
    {
        var ex = Assert.ThrowsExactly<UnsupportedMacOsVersionException>(
            () => MacOsMinimumOperatingSystemTable.Map(version, useBeta: true));

        Assert.IsFalse(ex.RequiresBetaOnlyFlag);
        Assert.AreEqual(version, ex.MinimumOsVersion);
    }

    [TestMethod]
    public void Map_TrimsWhitespace()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map(" 13.0 ", useBeta: false);

        Assert.IsTrue(payload.V13_0);
    }
}
