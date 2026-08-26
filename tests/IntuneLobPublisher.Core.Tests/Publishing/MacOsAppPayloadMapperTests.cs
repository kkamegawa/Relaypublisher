using System.Text.Json;
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
    public void Map_Pkg_ExplicitPrimaryIsFirstWithoutMutatingManifestOrder()
    {
        var manifest = CreateManifest(appType: "pkg");
        var app = manifest.Apps[0];
        app.Detection!.PrimaryBundleId = "com.contoso.tool";
        app.Detection.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "1.0.0" },
            new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "1.2.3", BundleBuildVersion = "999" },
            new IncludedAppManifest { BundleId = "com.contoso.agent", BundleVersion = "1.1.0" },
        ];

        var payload = (MacOsPkgAppPayload)MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);

        Assert.AreEqual("com.contoso.tool", payload.PrimaryBundleId);
        Assert.AreEqual("1.2.3", payload.PrimaryBundleVersion);
        CollectionAssert.AreEqual(
            new[] { "com.contoso.tool", "com.contoso.helper", "com.contoso.agent" },
            payload.IncludedApps.Select(item => item.BundleId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "com.contoso.helper", "com.contoso.tool", "com.contoso.agent" },
            app.Detection.IncludedApps.Select(item => item.BundleId).ToArray());
        var json = JsonSerializer.Serialize(payload);
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("999"),
            "BundleBuildVersion is not part of the macOSPkgApp wire contract.");
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

        Assert.AreEqual("com.contoso.tool", payload.BundleId);
        Assert.AreEqual("1.2.3", payload.BuildNumber);
        Assert.AreEqual("1234", payload.VersionNumber);
        Assert.HasCount(1, payload.ChildApps);
        Assert.AreEqual("com.contoso.tool", payload.ChildApps[0].BundleId);
        Assert.AreEqual("1.2.3", payload.ChildApps[0].BuildNumber);
        Assert.AreEqual("1234", payload.ChildApps[0].VersionNumber);
        Assert.AreEqual("#microsoft.graph.macOSLobApp", payload.ODataType);
    }

    [TestMethod]
    public void Map_Lob_ExplicitPrimaryMapsIndependentVersionsAndStableChildOrder()
    {
        var manifest = CreateManifest(appType: "lob");
        var app = manifest.Apps[0];
        app.Requirements!.MinimumOSVersion = "13.0";
        app.Detection!.PrimaryBundleId = "com.contoso.tool";
        app.Detection.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "1.0", BundleBuildVersion = "100" },
            new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "2.0", BundleBuildVersion = "205" },
            new IncludedAppManifest { BundleId = "com.contoso.agent", BundleVersion = "1.5", BundleBuildVersion = "150" },
        ];

        var payload = (MacOsLobAppPayload)MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);

        Assert.AreEqual("com.contoso.tool", payload.BundleId);
        Assert.AreEqual("2.0", payload.BuildNumber);
        Assert.AreEqual("205", payload.VersionNumber);
        CollectionAssert.AreEqual(
            new[] { "com.contoso.tool", "com.contoso.helper", "com.contoso.agent" },
            payload.ChildApps.Select(item => item.BundleId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "205", "100", "150" }, payload.ChildApps.Select(item => item.VersionNumber).ToArray());
        CollectionAssert.AreEqual(
            new[] { "com.contoso.helper", "com.contoso.tool", "com.contoso.agent" },
            app.Detection.IncludedApps.Select(item => item.BundleId).ToArray());
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

    [TestMethod]
    public void Map_PkgWithScripts_SetsPreAndPostInstallScriptContent()
    {
        var manifest = CreateManifest(appType: null);
        var scripts = new MacOsAppScripts(PreInstall: "cHJlLWluc3RhbGw=", PostInstall: "cG9zdC1pbnN0YWxs");

        var payload = (MacOsPkgAppPayload)MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null, scripts: scripts);

        Assert.AreEqual("cHJlLWluc3RhbGw=", payload.PreInstallScript?.ScriptContent);
        Assert.AreEqual("cG9zdC1pbnN0YWxs", payload.PostInstallScript?.ScriptContent);
        Assert.AreEqual("microsoft.graph.macOSAppScript", payload.PreInstallScript?.ODataType);
    }

    [TestMethod]
    public void Map_PkgWithOnlyPreInstallScript_LeavesPostInstallScriptNull()
    {
        var manifest = CreateManifest(appType: null);
        var scripts = new MacOsAppScripts(PreInstall: "cHJlLWluc3RhbGw=", PostInstall: null);

        var payload = (MacOsPkgAppPayload)MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null, scripts: scripts);

        Assert.IsNotNull(payload.PreInstallScript);
        Assert.IsNull(payload.PostInstallScript);
    }

    [TestMethod]
    public void Map_PkgWithoutScripts_LeavesScriptPropertiesNull()
    {
        var manifest = CreateManifest(appType: null);

        var payload = (MacOsPkgAppPayload)MacOsAppPayloadMapper.Map(manifest, manifest.Apps[0], iconBytes: null);

        Assert.IsNull(payload.PreInstallScript);
        Assert.IsNull(payload.PostInstallScript);
    }

    [TestMethod]
    public void Map_Lob_NeverExposesScriptProperties()
    {
        // AppType: lob has no preInstallScript/postInstallScript on Graph, and validation forbids
        // Scripts there in the first place - the mapper simply has nowhere to put them on MacOsLobAppPayload.
        var manifest = CreateManifest(appType: "lob");
        var app = manifest.Apps[0];
        app.Requirements!.MinimumOSVersion = "13.0";

        var payload = MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);

        Assert.IsInstanceOfType<MacOsLobAppPayload>(payload);
    }

    [TestMethod]
    public void Map_Lob_SerializedJsonOmitsBetaOnlyMinimumOsFlags()
    {
        // Regression test: v1.0's macOSMinimumOperatingSystem has no v14_0/v15_0/v26_0 property, so
        // serializing them as a literal "false" on a macOSLobApp (v1.0) request makes Graph reject the
        // whole call with 400 "The property 'v14_0' does not exist on type ...".
        var manifest = CreateManifest(appType: "lob");
        var app = manifest.Apps[0];
        app.Requirements!.MinimumOSVersion = "13.0";

        var payload = MacOsAppPayloadMapper.Map(manifest, app, iconBytes: null);
        var json = JsonSerializer.Serialize(payload, payload.GetType());

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("\"v14_0\""));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("\"v15_0\""));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("\"v26_0\""));
        StringAssert.Contains(json, "\"v13_0\":true");
    }
}
