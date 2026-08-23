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

        var flags = new[]
        {
            payload.V10_13, payload.V10_14, payload.V10_15, payload.V11_0, payload.V12_0, payload.V13_0,
            payload.V14_0 == true, payload.V15_0 == true, payload.V26_0 == true,
        };
        Assert.AreEqual(1, flags.Count(f => f));
    }

    [TestMethod]
    [DataRow("10.13")]
    [DataRow("11")]
    [DataRow("13")]
    public void Map_V1Version_LeavesBetaOnlyFlagsNull(string version)
    {
        // v1.0's macOSMinimumOperatingSystem has no v14_0/v15_0/v26_0 property at all, so these must
        // stay null (and therefore be omitted from the JSON) rather than serialize as a literal false,
        // which Graph rejects on a macOSLobApp (v1.0) request.
        var payload = MacOsMinimumOperatingSystemTable.Map(version, useBeta: false);

        Assert.IsNull(payload.V14_0);
        Assert.IsNull(payload.V15_0);
        Assert.IsNull(payload.V26_0);
    }

    [TestMethod]
    public void Map_MacOs14WithUseBetaTrue_SetsV14Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("14.0", useBeta: true);

        Assert.AreEqual(true, payload.V14_0);
        Assert.IsFalse(payload.V13_0);
    }

    [TestMethod]
    public void Map_MacOs15WithUseBetaTrue_SetsV15Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("15", useBeta: true);

        Assert.AreEqual(true, payload.V15_0);
    }

    [TestMethod]
    public void Map_MacOs26WithUseBetaTrue_SetsV26Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("26.0", useBeta: true);

        Assert.AreEqual(true, payload.V26_0);
        Assert.IsNull(payload.V14_0);
        Assert.IsNull(payload.V15_0);
    }

    [TestMethod]
    [DataRow("14")]
    [DataRow("14.0")]
    [DataRow("15")]
    [DataRow("15.0")]
    [DataRow("26")]
    [DataRow("26.0")]
    public void Map_BetaOnlyVersionWithUseBetaFalse_ThrowsRequiresBetaOnlyFlag(string version)
    {
        var ex = Assert.ThrowsExactly<UnsupportedMacOsVersionException>(
            () => MacOsMinimumOperatingSystemTable.Map(version, useBeta: false));

        Assert.IsTrue(ex.RequiresBetaOnlyFlag);
        StringAssert.Contains(ex.Message, "AppType 'pkg'");
    }

    [TestMethod]
    [DataRow("9.0")]
    [DataRow("27.0")]
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
