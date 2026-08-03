using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class MacOsAppPayloadMapperTests
{
    private static IntunePackageManifest CreateManifest(string? appType)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp(appType: appType)];
        return manifest;
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("pkg")]
    public void ResolveTarget_Pkg_UsesBetaAndPkgODataType(string? appType)
    {
        var app = TestManifests.CreateValidMacOsApp(appType: appType);

        var target = MacOsAppPayloadMapper.ResolveTarget(app);

        Assert.IsTrue(target.UseBeta);
        Assert.AreEqual("#microsoft.graph.macOSPkgApp", target.ODataType);
    }

    [TestMethod]
    public void ResolveTarget_Lob_UsesV1AndLobODataType()
    {
        var app = TestManifests.CreateValidMacOsApp(appType: "lob");

        var target = MacOsAppPayloadMapper.ResolveTarget(app);

        Assert.IsFalse(target.UseBeta);
        Assert.AreEqual("#microsoft.graph.macOSLobApp", target.ODataType);
    }

    [TestMethod]
    public void Map_Pkg_ReturnsPkgPayloadWithPrimaryBundleFromFirstIncludedApp()
    {
        var manifest = CreateManifest(appType: null);
        var app = manifest.Apps[0];
        app.Detection!.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "1.2.3" },
            new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "1.0.0" },
        ];

        var payload = (MacOsPkgAppPayload)MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);

        Assert.AreEqual("com.contoso.tool", payload.PrimaryBundleId);
        Assert.AreEqual("1.2.3", payload.PrimaryBundleVersion);
        Assert.HasCount(2, payload.IncludedApps);
        Assert.AreEqual("com.contoso.helper", payload.IncludedApps[1].BundleId);
        Assert.AreEqual(app.Source!.Destination, payload.FileName);
        Assert.IsFalse(payload.IgnoreVersionDetection);
        Assert.AreEqual("#microsoft.graph.macOSPkgApp", payload.ODataType);
    }

    [TestMethod]
    public void Map_Lob_ReturnsLobPayloadWithChildAppsShapeDifferentFromPkg()
    {
        var manifest = CreateManifest(appType: "lob");
        var app = manifest.Apps[0];
        // AppType: lob stays on Graph v1.0, which has no macOS 14+ flag (see MacOsMinimumOperatingSystemTable);
        // the default fixture's "14.0" is only valid for AppType: pkg (beta).
        app.Requirements!.MinimumOSVersion = "13.0";

        var payload = (MacOsLobAppPayload)MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);

        Assert.AreEqual("1.2.3", payload.BuildNumber);
        Assert.AreEqual("1.2.3", payload.VersionNumber);
        Assert.HasCount(1, payload.ChildApps);
        Assert.AreEqual("com.contoso.tool", payload.ChildApps[0].BundleId);
        Assert.AreEqual("1.2.3", payload.ChildApps[0].BuildNumber);
        Assert.AreEqual("1.2.3", payload.ChildApps[0].VersionNumber);
        Assert.AreEqual("#microsoft.graph.macOSLobApp", payload.ODataType);
    }

    [TestMethod]
    public void Map_IgnoreAppVersionTrue_SetsIgnoreVersionDetection()
    {
        var manifest = CreateManifest(appType: null);
        manifest.Apps[0].Detection!.IgnoreAppVersion = true;

        var payload = MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null);

        Assert.IsTrue(payload.IgnoreVersionDetection);
    }

    [TestMethod]
    public void Map_CreateNotes_OnlySetWhenPassed()
    {
        var manifest = CreateManifest(appType: null);

        var withoutNotes = MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null);
        var withNotes = MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null, notes: "{}");

        Assert.IsNull(withoutNotes.Notes);
        Assert.AreEqual("{}", withNotes.Notes);
    }

    [TestMethod]
    public void Map_IconBytesProvided_SetsLargeIcon()
    {
        var manifest = CreateManifest(appType: null);
        manifest.Icon = "assets/icons/tool.png";

        var payload = MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: [1, 2, 3]);

        Assert.IsNotNull(payload.LargeIcon);
        Assert.AreEqual("image/png", payload.LargeIcon!.Type);
    }

    [TestMethod]
    public void Map_MinimumSupportedOperatingSystem_MatchesRequirementsVersion()
    {
        var manifest = CreateManifest(appType: null);
        manifest.Apps[0].Requirements!.MinimumOSVersion = "13.0";

        var payload = MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null);

        Assert.IsTrue(payload.MinimumSupportedOperatingSystem.V13_0);
    }
}
