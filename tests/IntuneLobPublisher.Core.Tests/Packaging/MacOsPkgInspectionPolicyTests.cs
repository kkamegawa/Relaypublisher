using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests.Packaging;

[TestClass]
public sealed class MacOsPkgInspectionPolicyTests
{
    [TestMethod]
    public void CreateReport_MultipleAndUndeclaredBundles_ReturnsStableWarningCodes()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        manifest.Apps = [app];
        var inspection = new PkgBundleInspectionResult("1",
        [
            new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo"),
            new PkgBundleIdentity("com.contoso.helper", "1.0", null, "PackageInfo"),
        ]);

        var report = MacOsPkgInspectionPolicy.CreateReport(manifest, app, inspection);

        CollectionAssert.AreEqual(
            new[]
            {
                PkgInspectionWarningCode.MultipleBundlesWithoutExplicitPrimary,
                PkgInspectionWarningCode.PackageBundleNotDeclared,
            },
            report.Warnings.Select(warning => warning.Code).ToArray());
    }

    [TestMethod]
    public void CreateReport_VersionMismatchWithIgnoreAppVersion_RemainsVisible()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.IgnoreAppVersion = true;
        manifest.Apps = [app];
        var inspection = new PkgBundleInspectionResult("1",
        [new PkgBundleIdentity("com.contoso.tool", "9.9", null, "PackageInfo")]);

        var report = MacOsPkgInspectionPolicy.CreateReport(manifest, app, inspection);

        Assert.IsTrue(report.Warnings.Any(warning => warning.Code == PkgInspectionWarningCode.ManifestBundleVersionMismatch));
    }

    [TestMethod]
    public void CreateReport_MissingDeclaredBundle_ReturnsNoBundlesDetectedAndManifestBundleNotFound()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        manifest.Apps = [app];
        var inspection = new PkgBundleInspectionResult("1", []);

        var report = MacOsPkgInspectionPolicy.CreateReport(manifest, app, inspection);

        CollectionAssert.AreEqual(
            new[]
            {
                PkgInspectionWarningCode.NoBundlesDetected,
                PkgInspectionWarningCode.ManifestBundleNotFound,
            },
            report.Warnings.Select(warning => warning.Code).ToArray());
        Assert.AreEqual("com.contoso.tool", report.SelectedPrimaryBundleId);
    }

    [TestMethod]
    public void CreateReport_ZeroDetectedBundles_ReturnsNoBundlesDetected()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        manifest.Apps = [app];
        var inspection = new PkgBundleInspectionResult("1", []);

        var report = MacOsPkgInspectionPolicy.CreateReport(manifest, app, inspection);

        Assert.IsTrue(report.Warnings.Any(warning => warning.Code == PkgInspectionWarningCode.NoBundlesDetected));
    }
}
