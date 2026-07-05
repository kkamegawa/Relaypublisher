using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class PathSafetyTests
{
    [TestMethod]
    [DataRow("../evil.ps1")]
    [DataRow("..\\evil.ps1")]
    [DataRow("bin/../../evil.ps1")]
    [DataRow("C:\\evil.ps1")]
    [DataRow("c:/evil.ps1")]
    [DataRow("/evil.ps1")]
    [DataRow("\\evil.ps1")]
    public void EnsureSafeRelativePath_UnsafeInput_Throws(string value)
    {
        Assert.ThrowsExactly<UnsafePathException>(
            () => PathSafety.EnsureSafeRelativePath(value, "Destination"));
    }

    [TestMethod]
    [DataRow("install.ps1")]
    [DataRow("bin/app.exe")]
    [DataRow("bin\\nested\\app.exe")]
    public void EnsureSafeRelativePath_SafeInput_Passes(string value)
    {
        PathSafety.EnsureSafeRelativePath(value, "Destination");
    }

    [TestMethod]
    public void EnsureSafeRelativePath_Empty_Throws()
    {
        Assert.ThrowsExactly<UnsafePathException>(
            () => PathSafety.EnsureSafeRelativePath(string.Empty, "Destination"));
    }

    [TestMethod]
    [DataRow("../evil.ps1")]
    [DataRow("..\\evil.ps1")]
    [DataRow("bin/../../evil.ps1")]
    [DataRow("C:\\evil.ps1")]
    [DataRow("c:/evil.ps1")]
    [DataRow("/evil.ps1")]
    [DataRow("\\evil.ps1")]
    [DataRow("")]
    public void IsSafeRelativePath_UnsafeInput_ReturnsFalse(string value)
        => Assert.IsFalse(PathSafety.IsSafeRelativePath(value));

    [TestMethod]
    [DataRow("install.ps1")]
    [DataRow("bin/app.exe")]
    [DataRow("bin\\nested\\app.exe")]
    public void IsSafeRelativePath_SafeInput_ReturnsTrue(string value)
        => Assert.IsTrue(PathSafety.IsSafeRelativePath(value));

    [TestMethod]
    public void ResolveWithin_NestedPath_ReturnsPathUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "staging-root");

        var resolved = PathSafety.ResolveWithin(root, "bin/app.exe", "Destination");

        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, resolved);
        Assert.EndsWith("app.exe", resolved);
    }

    [TestMethod]
    public void EnsureSafeDirectoryName_PackageIdentifier_Passes()
    {
        PathSafety.EnsureSafeDirectoryName("Contoso.Tool", "PackageIdentifier");
    }

    [TestMethod]
    [DataRow("a/b")]
    [DataRow("a\\b")]
    [DataRow("..")]
    [DataRow("a:b")]
    public void EnsureSafeDirectoryName_UnsafeInput_Throws(string value)
    {
        Assert.ThrowsExactly<UnsafePathException>(
            () => PathSafety.EnsureSafeDirectoryName(value, "PackageIdentifier"));
    }
}
