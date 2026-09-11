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
    [DataRow("12.0")]
    [DataRow("13")]
    [DataRow("13.0")]
    [DataRow("14")]
    [DataRow("14.0")]
    [DataRow("15")]
    [DataRow("15.0")]
    [DataRow("26")]
    [DataRow("26.0")]
    public void Map_KnownVersion_SetsExactlyOneFlag(string version)
    {
        var payload = MacOsMinimumOperatingSystemTable.Map(version);

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
    public void Map_PreV14Version_LeavesBetaOnlyFlagsNull(string version)
    {
        // Only the matched version's flag is ever set to true; the v14_0/v15_0/v26_0 flags stay null
        // (and therefore omitted from the JSON) for any version below macOS 14, keeping the payload
        // minimal even though both macOSPkgApp and macOSLobApp now always target Graph beta.
        var payload = MacOsMinimumOperatingSystemTable.Map(version);

        Assert.IsNull(payload.V14_0);
        Assert.IsNull(payload.V15_0);
        Assert.IsNull(payload.V26_0);
    }

    [TestMethod]
    public void Map_MacOs14_SetsV14Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("14.0");

        Assert.AreEqual(true, payload.V14_0);
        Assert.IsFalse(payload.V13_0);
    }

    [TestMethod]
    public void Map_MacOs15_SetsV15Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("15");

        Assert.AreEqual(true, payload.V15_0);
    }

    [TestMethod]
    public void Map_MacOs26_SetsV26Flag()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map("26.0");

        Assert.AreEqual(true, payload.V26_0);
        Assert.IsNull(payload.V14_0);
        Assert.IsNull(payload.V15_0);
    }

    [TestMethod]
    [DataRow("9.0")]
    [DataRow("27.0")]
    [DataRow("not-a-version")]
    public void Map_UnknownVersion_Throws(string version)
    {
        var ex = Assert.ThrowsExactly<UnsupportedMacOsVersionException>(
            () => MacOsMinimumOperatingSystemTable.Map(version));

        Assert.AreEqual(version, ex.MinimumOsVersion);
    }

    [TestMethod]
    public void Map_TrimsWhitespace()
    {
        var payload = MacOsMinimumOperatingSystemTable.Map(" 13.0 ");

        Assert.IsTrue(payload.V13_0);
    }
}
