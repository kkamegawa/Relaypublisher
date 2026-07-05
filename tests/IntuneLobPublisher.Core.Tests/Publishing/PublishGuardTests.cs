using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class PublishGuardTests
{
    [TestMethod]
    [DataRow("1.0.0", "1.0.0", 0)]
    [DataRow("1.2.3", "1.2.4", -1)]
    [DataRow("1.10.0", "1.9.0", 1)]
    [DataRow("2.0", "1.9.9", 1)]
    [DataRow("1.0", "1.0.0", 0)]
    public void CompareVersions_NumericSegments_ComparesNumerically(string left, string right, int expectedSign)
    {
        var result = PublishGuard.CompareVersions(left, right);

        Assert.AreEqual(expectedSign, Math.Sign(result));
    }

    [TestMethod]
    public void EvaluateVersion_NewApp_AlwaysProceeds()
    {
        var result = PublishGuard.EvaluateVersion(storedPackageVersion: null, manifestPackageVersion: "1.0.0", allowDowngrade: false);

        Assert.AreEqual(VersionGuardResult.Proceed, result);
    }

    [TestMethod]
    public void EvaluateVersion_ManifestVersionLower_SkipsByDefault()
    {
        var result = PublishGuard.EvaluateVersion(storedPackageVersion: "1.2.0", manifestPackageVersion: "1.1.0", allowDowngrade: false);

        Assert.AreEqual(VersionGuardResult.SkipDowngrade, result);
    }

    [TestMethod]
    public void EvaluateVersion_ManifestVersionLowerWithAllowDowngrade_Proceeds()
    {
        var result = PublishGuard.EvaluateVersion(storedPackageVersion: "1.2.0", manifestPackageVersion: "1.1.0", allowDowngrade: true);

        Assert.AreEqual(VersionGuardResult.Proceed, result);
    }

    [TestMethod]
    public void EvaluateVersion_ManifestVersionEqualOrHigher_Proceeds()
    {
        Assert.AreEqual(VersionGuardResult.Proceed, PublishGuard.EvaluateVersion("1.2.0", "1.2.0", allowDowngrade: false));
        Assert.AreEqual(VersionGuardResult.Proceed, PublishGuard.EvaluateVersion("1.2.0", "1.3.0", allowDowngrade: false));
    }

    [TestMethod]
    public void EvaluateContentUpload_NewApp_AlwaysUploads()
    {
        var result = PublishGuard.EvaluateContentUpload(storedInputHash: null, manifestInputHash: "hash-1");

        Assert.AreEqual(ContentUploadDecision.Upload, result);
    }

    [TestMethod]
    public void EvaluateContentUpload_MatchingHash_Skips()
    {
        var result = PublishGuard.EvaluateContentUpload(storedInputHash: "hash-1", manifestInputHash: "hash-1");

        Assert.AreEqual(ContentUploadDecision.Skip, result);
    }

    [TestMethod]
    public void EvaluateContentUpload_DifferentHash_Uploads()
    {
        var result = PublishGuard.EvaluateContentUpload(storedInputHash: "hash-1", manifestInputHash: "hash-2");

        Assert.AreEqual(ContentUploadDecision.Upload, result);
    }
}
