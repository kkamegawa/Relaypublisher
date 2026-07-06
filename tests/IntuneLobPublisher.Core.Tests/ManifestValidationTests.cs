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
}
