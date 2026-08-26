using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class ManifestValidationTests
{
    private readonly ManifestValidator _validator = new();

    private static void AssertInvalid(IntunePackageManifest manifest, string expectedMessageFragment)
    {
        var result = new ManifestValidator().Validate(manifest);
        Assert.IsFalse(result.IsValid, "Expected the manifest to be invalid.");
        Assert.IsTrue(
            result.Errors.Any(e =>
                e.ErrorMessage.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase)
                || e.PropertyName.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase)),
            $"Expected an error mentioning '{expectedMessageFragment}' but got: " +
            string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_ValidWindowsX64Manifest_Passes()
    {
        var result = _validator.Validate(TestManifests.CreateValid("x64"));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_ValidWindowsArm64Manifest_Passes()
    {
        var result = _validator.Validate(TestManifests.CreateValid("arm64"));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    [DataRow("C:\\evil.png")]
    [DataRow("c:/evil.png")]
    [DataRow("../evil.png")]
    [DataRow("/etc/evil.png")]
    public void Validate_IconEscapesRepository_Fails(string icon)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = icon;
        AssertInvalid(manifest, "Icon");
    }

    [TestMethod]
    [DataRow("assets/icons/contoso-tool.png")]
    [DataRow("assets/icons/contoso-tool.PNG")]
    [DataRow("assets/icons/contoso-tool.jpg")]
    [DataRow("assets/icons/contoso-tool.jpeg")]
    public void Validate_SupportedIconExtension_Passes(string icon)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = icon;
        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    [DataRow("assets/icons/contoso-tool.bmp")]
    [DataRow("assets/icons/contoso-tool.gif")]
    [DataRow("assets/icons/contoso-tool")]
    public void Validate_UnsupportedIconExtension_Fails(string icon)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = icon;
        AssertInvalid(manifest, "unsupported file extension");
    }

    [TestMethod]
    public void Validate_MissingSchemaVersion_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.SchemaVersion = null;
        AssertInvalid(manifest, "SchemaVersion");
    }

    [TestMethod]
    public void Validate_UnknownSchemaVersionMajor_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.SchemaVersion = "2.0";
        AssertInvalid(manifest, "unsupported major");
    }

    [TestMethod]
    public void Validate_MissingPackageIdentifier_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.PackageIdentifier = null;
        AssertInvalid(manifest, "PackageIdentifier");
    }

    [TestMethod]
    public void Validate_MissingPackageVersion_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.PackageVersion = null;
        AssertInvalid(manifest, "PackageVersion");
    }

    [TestMethod]
    public void Validate_MissingApps_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [];
        AssertInvalid(manifest, "Apps");
    }

    [TestMethod]
    public void Validate_UnsupportedPlatform_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Platform = "linux";
        AssertInvalid(manifest, "Platform 'linux'");
    }

    [TestMethod]
    public void Validate_UnsupportedArchitecture_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Architecture = "x86";
        AssertInvalid(manifest, "Architecture 'x86'");
    }

    [TestMethod]
    public void Validate_UnsupportedInstallerType_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].InstallerType = "msi";
        AssertInvalid(manifest, "InstallerType 'msi'");
    }

    [TestMethod]
    public void Validate_MissingDisplayName_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].DisplayName = null;
        AssertInvalid(manifest, "DisplayName");
    }

    [TestMethod]
    public void Validate_MissingSetupFile_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.IntuneWin!.SetupFile = null;
        AssertInvalid(manifest, "SetupFile");
    }

    [TestMethod]
    public void Validate_MissingInstallCommandLine_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Install!.CommandLine = null;
        AssertInvalid(manifest, "CommandLine");
    }

    [TestMethod]
    public void Validate_MissingUninstallCommandLine_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Install!.UninstallCommandLine = null;
        AssertInvalid(manifest, "UninstallCommandLine");
    }

    [TestMethod]
    public void Validate_UnsupportedInstallExperience_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Install!.InstallExperience = "admin";
        AssertInvalid(manifest, "InstallExperience 'admin'");
    }

    [TestMethod]
    public void Validate_UnsupportedRestartBehavior_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Install!.RestartBehavior = "reboot";
        AssertInvalid(manifest, "RestartBehavior 'reboot'");
    }

    [TestMethod]
    public void Validate_UnsupportedDetectionType_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Detection!.Type = "registry";
        AssertInvalid(manifest, "Detection.Type 'registry'");
    }

    [TestMethod]
    public void Validate_ScriptDetectionWithoutScriptFile_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Detection!.ScriptFile = null;
        AssertInvalid(manifest, "ScriptFile");
    }

    [TestMethod]
    public void Validate_MissingMinimumOSVersion_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Requirements!.MinimumOSVersion = null;
        AssertInvalid(manifest, "MinimumOSVersion");
    }

    [TestMethod]
    public void Validate_RequirementsArchitectureMismatch_Fails()
    {
        var manifest = TestManifests.CreateValid("x64");
        manifest.Apps[0].Requirements!.Architecture = "arm64";
        AssertInvalid(manifest, "must match the app Architecture");
    }

    [TestMethod]
    public void Validate_InvalidAssignmentGroupId_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments[0].GroupId = "not-a-guid";
        AssertInvalid(manifest, "GroupId 'not-a-guid'");
    }

    [TestMethod]
    public void Validate_InvalidAssignmentIntent_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments[0].Intent = "mandatory";
        AssertInvalid(manifest, "Intent 'mandatory'");
    }

    [TestMethod]
    public void Validate_GroupIdOnAllDevicesTarget_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments[0] = new AssignmentManifest
        {
            Target = "allDevices",
            GroupId = "00000000-0000-0000-0000-000000000001",
            Intent = "required",
        };
        AssertInvalid(manifest, "GroupId must not be set");
    }

    [TestMethod]
    public void Validate_DuplicateAssignmentTargets_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments.Add(new AssignmentManifest
        {
            Target = "group",
            GroupId = manifest.Apps[0].Assignments[0].GroupId,
            Intent = "available",
        });
        AssertInvalid(manifest, "duplicate targets");
    }

    [TestMethod]
    public void Validate_SameGroupIncludeAndExclude_IsNotADuplicate()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments.Add(new AssignmentManifest
        {
            Target = "group",
            GroupId = manifest.Apps[0].Assignments[0].GroupId,
            Mode = "exclude",
        });

        var result = _validator.Validate(manifest);

        Assert.IsFalse(
            result.Errors.Any(e => e.ErrorMessage.Contains("duplicate targets", StringComparison.OrdinalIgnoreCase)),
            "Include and exclude assignments for the same group are distinct Graph targets, not duplicates.");
    }

    [TestMethod]
    public void Validate_FilterIdWithoutFilterMode_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Assignments[0].FilterId = "00000000-0000-0000-0000-00000000000f";
        manifest.Apps[0].Assignments[0].FilterMode = null;
        AssertInvalid(manifest, "FilterMode");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("pkg")]
    public void Validate_MacOsPkgWithUninstallIntent_Fails(string? appType)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Platform = "macos";
        manifest.Apps[0].AppType = appType;
        manifest.Apps[0].Assignments[0].Intent = "uninstall";
        AssertInvalid(manifest, "Intent 'uninstall' is not supported for macOS AppType 'pkg'");
    }

    [TestMethod]
    public void Validate_MacOsLobWithUninstallIntent_HasNoPkgUninstallError()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Platform = "macos";
        manifest.Apps[0].AppType = "lob";
        manifest.Apps[0].Assignments[0].Intent = "uninstall";
        var result = _validator.Validate(manifest);
        Assert.IsFalse(
            result.Errors.Any(e => e.ErrorMessage.Contains("uninstall", StringComparison.OrdinalIgnoreCase)),
            "AppType 'lob' must not trigger the pkg uninstall rule.");
    }

    [TestMethod]
    public void Validate_InvalidSha256Format_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.ExternalFiles[0].Sha256 = "zz-not-hex";
        AssertInvalid(manifest, "Sha256");
    }

    [TestMethod]
    public void Validate_TokenAuthWithoutSecretName_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.ExternalFiles[0].Auth = new AuthManifest { Type = "token" };
        AssertInvalid(manifest, "Auth.SecretName");
    }

    [TestMethod]
    public void Validate_GitHubReleaseWithWorkloadIdentity_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.ExternalFiles[0] = new SourceManifest
        {
            Type = "githubRelease",
            Owner = "contoso",
            Repository = "tools",
            Tag = "v1.0.0",
            AssetName = "tool.exe",
            Destination = "bin/tool.exe",
            Sha256 = new string('a', 64),
            Auth = new AuthManifest { Type = "workloadIdentity" },
        };
        AssertInvalid(manifest, "workloadIdentity");
    }

    [TestMethod]
    public void Validate_AzureBlobWithoutWorkloadIdentity_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.ExternalFiles[0] = new SourceManifest
        {
            Type = "azureBlob",
            AccountName = "contosopackages",
            Container = "intune-packages",
            BlobName = "windows/tool.exe",
            Destination = "bin/tool.exe",
            Sha256 = new string('a', 64),
        };
        AssertInvalid(manifest, "workloadIdentity");
    }

    [TestMethod]
    public void Validate_AzureBlobWithWorkloadIdentity_Passes()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Package!.ExternalFiles[0] = new SourceManifest
        {
            Type = "azureBlob",
            AccountName = "contosopackages",
            Container = "intune-packages",
            BlobName = "windows/tool.exe",
            Destination = "bin/tool.exe",
            Sha256 = new string('a', 64),
            Auth = new AuthManifest { Type = "workloadIdentity" },
        };

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_InvalidAssignmentSync_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.AssignmentSync = "overwrite";
        AssertInvalid(manifest, "AssignmentSync 'overwrite'");
    }

    [TestMethod]
    public void Validate_IconWithTraversal_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "../outside/icon.png";
        AssertInvalid(manifest, "Icon");
    }

    [TestMethod]
    public void Validate_ValidMacOsPkgManifest_Passes()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp()];

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_ValidMacOsPkgManifest_DefaultAppTypeIsPkg()
    {
        // AppType omitted (null) is equivalent to "pkg" (doc/01-manifest-schema.md §5.4).
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp(appType: null)];

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_ValidMacOsLobManifest_WithIcon_Passes()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        manifest.Apps = [TestManifests.CreateValidMacOsApp(appType: "lob")];

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_MacOsLobManifest_WithoutIcon_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = null;
        manifest.Apps = [TestManifests.CreateValidMacOsApp(appType: "lob")];

        AssertInvalid(manifest, "Icon is required");
    }

    [TestMethod]
    public void Validate_MacOsEmptyIncludedApps_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Detection!.IncludedApps = [];
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Detection.IncludedApps is required");
    }

    [TestMethod]
    public void Validate_MacOsIncludedAppMissingBundleId_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Detection!.IncludedApps = [new IncludedAppManifest { BundleId = null, BundleVersion = "1.0" }];
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "BundleId");
    }

    [TestMethod]
    [DataRow("com.contoso.helper")]
    [DataRow("com.contoso")]
    public void Validate_MacOsPrimaryBundleUniqueExactOrSegmentPrefix_Passes(string primaryBundleId)
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.PrimaryBundleId = primaryBundleId;
        app.Detection.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.example.tool", BundleVersion = "1.0" },
            new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "2.0" },
        ];
        manifest.Apps = [app];

        var result = _validator.Validate(manifest);

        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    [DataRow("com.contoso.missing")]
    [DataRow("COM.CONTOSO.HELPER")]
    [DataRow("com.contoso.help")]
    public void Validate_MacOsPrimaryBundleWithoutOrdinalSegmentMatch_Fails(string primaryBundleId)
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.PrimaryBundleId = primaryBundleId;
        app.Detection.IncludedApps =
        [new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "2.0" }];
        manifest.Apps = [app];

        AssertInvalid(manifest, "did not match");
    }

    [TestMethod]
    public void Validate_MacOsPrimaryBundleWithMultipleSegmentMatches_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.PrimaryBundleId = "com.contoso";
        app.Detection.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.contoso.client", BundleVersion = "1.0" },
            new IncludedAppManifest { BundleId = "com.contoso.agent", BundleVersion = "1.0" },
        ];
        manifest.Apps = [app];

        AssertInvalid(manifest, "matched more than one");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void Validate_MacOsPrimaryBundleBlank_Fails(string primaryBundleId)
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.PrimaryBundleId = primaryBundleId;
        manifest.Apps = [app];

        AssertInvalid(manifest, "must not be empty or whitespace");
    }

    [TestMethod]
    public void Validate_WindowsPrimaryBundleId_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Detection!.PrimaryBundleId = "com.contoso.tool";

        AssertInvalid(manifest, "PrimaryBundleId must not be set");
    }

    [TestMethod]
    public void Validate_MacOsDuplicateBundleIds_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.IncludedApps =
        [
            new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "1.0" },
            new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "2.0" },
        ];
        manifest.Apps = [app];

        AssertInvalid(manifest, "duplicate BundleId");
    }

    [TestMethod]
    public void Validate_MacOsMoreThanFiveHundredIncludedApps_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.IncludedApps = Enumerable.Range(0, 501)
            .Select(index => new IncludedAppManifest { BundleId = $"com.contoso.app{index}", BundleVersion = "1.0" })
            .ToList();
        manifest.Apps = [app];

        AssertInvalid(manifest, "at most 500");
    }

    [TestMethod]
    public void Validate_MacOsLobMissingBundleBuildVersion_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/tool.png";
        var app = TestManifests.CreateValidMacOsApp(appType: "lob");
        app.Detection!.IncludedApps![0].BundleBuildVersion = null;
        manifest.Apps = [app];

        AssertInvalid(manifest, "BundleBuildVersion is required");
    }

    [TestMethod]
    public void Validate_MacOsPkgWithBundleBuildVersion_Passes()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.IncludedApps![0].BundleBuildVersion = "1234";
        manifest.Apps = [app];

        var result = _validator.Validate(manifest);

        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_MacOsAppWithWindowsPackage_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Package = TestManifests.CreateValidApp().Package;
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Package must not be set for Platform 'macos'");
    }

    [TestMethod]
    public void Validate_MacOsAppWithoutSource_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Source = null;
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Source is required for Platform 'macos'");
    }

    [TestMethod]
    public void Validate_MacOsAppWithInstallerTypeWin32_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.InstallerType = "win32";
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "InstallerType 'win32' is not supported for Platform 'macos'");
    }

    [TestMethod]
    public void Validate_MacOsAppWithUnsupportedAppType_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp(appType: "dmg");
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "AppType 'dmg' is not supported for Platform 'macos'");
    }

    [TestMethod]
    public void Validate_WindowsAppWithAppTypeSet_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].AppType = "pkg";

        AssertInvalid(manifest, "AppType must not be set for Platform 'windows'");
    }

    [TestMethod]
    public void Validate_MacOsPkgWithScripts_Passes()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest
        {
            PreInstall = "scripts/macos/contoso-tool/preinstall.sh",
            PostInstall = "scripts/macos/contoso-tool/postinstall.sh",
        };
        manifest.Apps = [macApp];

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_MacOsPkgWithOnlyPreInstallScript_Passes()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        var result = _validator.Validate(manifest);
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }

    [TestMethod]
    public void Validate_WindowsAppWithScripts_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Scripts = new MacOsScriptsManifest { PreInstall = "scripts/windows/preinstall.sh" };

        AssertInvalid(manifest, "Scripts must not be set for Platform 'windows'");
    }

    [TestMethod]
    public void Validate_MacOsLobWithScripts_Fails()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        var macApp = TestManifests.CreateValidMacOsApp(appType: "lob");
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Scripts must not be set for macOS AppType 'lob'");
    }

    [TestMethod]
    public void Validate_MacOsPkgWithEmptyScriptsBlock_Fails()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest();
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Scripts must set at least one of PreInstall or PostInstall");
    }

    [TestMethod]
    [DataRow("../outside/preinstall.sh")]
    [DataRow("/etc/preinstall.sh")]
    [DataRow("C:\\evil\\preinstall.sh")]
    public void Validate_MacOsScriptEscapesRepository_Fails(string path)
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = path };
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "Scripts.PreInstall");
    }

    [TestMethod]
    [DataRow("scripts/macos/contoso-tool/preinstall.ps1")]
    [DataRow("scripts/macos/contoso-tool/preinstall")]
    [DataRow("scripts/macos/contoso-tool/preinstall.bash")]
    public void Validate_MacOsScriptWithUnsupportedExtension_Fails(string path)
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = path };
        manifest.Apps = [macApp];

        AssertInvalid(manifest, "must have the '.sh' extension");
    }

    private static IntunePackageManifest ManifestWithCategories(List<string>? categories)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Categories = categories;
        return manifest;
    }

    [TestMethod]
    public void Validate_CategoriesOmitted_Passes()
    {
        var result = _validator.Validate(ManifestWithCategories(null));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_CategoriesEmptyList_Passes()
    {
        // An explicit empty list means "remove every relationship"; it is valid, not a mistake.
        var result = _validator.Validate(ManifestWithCategories([]));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_CategoriesWithDistinctNames_Passes()
    {
        var result = _validator.Validate(ManifestWithCategories(["Business Apps", "Productivity"]));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    public void Validate_CategoryNameWithInnerSpacesAndSymbols_Passes()
    {
        // No character-class, length or count restriction is imposed locally: the tenant catalog decides.
        var result = _validator.Validate(ManifestWithCategories(["Line-of-Business & Ops (JP)", "業務アプリ"]));
        Assert.IsTrue(result.IsValid, string.Join(" / ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("\t")]
    public void Validate_EmptyOrWhitespaceCategory_Fails(string category)
    {
        AssertInvalid(ManifestWithCategories([category]), "empty or whitespace-only");
    }

    [TestMethod]
    [DataRow(" Business Apps")]
    [DataRow("Business Apps ")]
    [DataRow("\tBusiness Apps")]
    public void Validate_CategoryWithOuterWhitespace_Fails(string category)
    {
        // Names are never trimmed, so a padded name would silently fail to resolve in the tenant.
        AssertInvalid(ManifestWithCategories([category]), "leading or trailing whitespace");
    }

    [TestMethod]
    [DataRow("Business Apps", "business apps")]
    [DataRow("Business Apps", "BUSINESS APPS")]
    [DataRow("Business Apps", "Business Apps")]
    public void Validate_CaseInsensitiveDuplicateCategories_Fails(string first, string second)
    {
        AssertInvalid(ManifestWithCategories([first, second]), "duplicate names");
    }
}
