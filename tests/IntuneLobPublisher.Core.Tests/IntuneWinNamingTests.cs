using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class IntuneWinNamingTests
{
    [TestMethod]
    public void PackageFileNameFor_FlatSetupFile_ReplacesExtension()
    {
        Assert.AreEqual("install.intunewin", IntuneWinNaming.PackageFileNameFor("install.ps1"));
    }

    [TestMethod]
    public void PackageFileNameFor_ForwardSlashSubdirectory_UsesBaseName()
    {
        Assert.AreEqual("setup.intunewin", IntuneWinNaming.PackageFileNameFor("sub/dir/setup.exe"));
    }

    /// <summary>
    /// `Path.GetFileNameWithoutExtension` only treats '\' as a separator on Windows, but this manifest
    /// value can be mapped on any OS (payload mapping is not Windows-only), so a manifest authored with
    /// backslashes must still resolve to the base name everywhere.
    /// </summary>
    [TestMethod]
    public void PackageFileNameFor_BackslashSubdirectory_UsesBaseNameOnAnyOs()
    {
        Assert.AreEqual("setup.intunewin", IntuneWinNaming.PackageFileNameFor("sub\\dir\\setup.exe"));
    }
}
