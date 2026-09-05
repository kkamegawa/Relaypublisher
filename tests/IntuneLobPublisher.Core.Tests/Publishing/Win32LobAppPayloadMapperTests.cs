using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class Win32LobAppPayloadMapperTests
{
    private const string DetectionScript = "Write-Output 'detected'";

    [TestMethod]
    public void Map_CopiesInstallExperienceAndCommandLines()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual(app.Install!.CommandLine, payload.InstallCommandLine);
        Assert.AreEqual(app.Install!.UninstallCommandLine, payload.UninstallCommandLine);
        Assert.AreEqual(app.Install!.InstallExperience, payload.InstallExperience.RunAsAccount);
        Assert.AreEqual(app.Install!.RestartBehavior, payload.InstallExperience.DeviceRestartBehavior);
        Assert.AreEqual(app.DisplayName, payload.DisplayName);
        Assert.AreEqual(manifest.Description, payload.Description);
        Assert.AreEqual(manifest.Publisher, payload.Publisher);
        Assert.AreEqual(manifest.PackageVersion, payload.DisplayVersion);
    }

    [TestMethod]
    public void Map_ReturnCodesOmitted_AppliesIntuneDefaultSet()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Install!.ReturnCodes = null;

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        CollectionAssert.AreEquivalent(
            new[] { (0, "success"), (1707, "success"), (3010, "softReboot"), (1641, "hardReboot"), (1618, "retry") },
            payload.ReturnCodes.Select(rc => (rc.ReturnCode, rc.Type)).ToArray());
    }

    [TestMethod]
    public void Map_ReturnCodesEmptyList_AppliesIntuneDefaultSet()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Install!.ReturnCodes = [];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.HasCount(5, payload.ReturnCodes);
    }

    [TestMethod]
    public void Map_ReturnCodesSupplied_UsesManifestValues()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Install!.ReturnCodes =
        [
            new ReturnCodeManifest { Code = 0, Type = "success" },
            new ReturnCodeManifest { Code = 3010, Type = "softReboot" },
        ];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.HasCount(2, payload.ReturnCodes);
        Assert.AreEqual(0, payload.ReturnCodes[0].ReturnCode);
        Assert.AreEqual("success", payload.ReturnCodes[0].Type);
        Assert.AreEqual(3010, payload.ReturnCodes[1].ReturnCode);
        Assert.AreEqual("softReboot", payload.ReturnCodes[1].Type);
    }

    [TestMethod]
    [DataRow("x64")]
    [DataRow("arm64")]
    public void Map_Architecture_SetsAllowedArchitecturesAndForcesApplicableToNone(string architecture)
    {
        var manifest = TestManifests.CreateValid(architecture);
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual(architecture, payload.AllowedArchitectures);
        Assert.AreEqual("none", payload.ApplicableArchitectures);
    }

    [TestMethod]
    public void Map_MinimumOsVersion_MapsToWindowsRelease()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Requirements!.MinimumOSVersion = "10.0.22621";

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual("Windows11_22H2", payload.MinimumSupportedWindowsRelease);
    }

    [TestMethod]
    public void Map_SetupFile_SetsSetupFilePathAndFileName()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Package!.IntuneWin!.SetupFile = "install.ps1";

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual("install.ps1", payload.SetupFilePath);
        Assert.AreEqual("install.intunewin", payload.FileName);
    }

    [TestMethod]
    public void Map_SetupFileInSubdirectory_NormalizesSetupFilePathAndUsesBaseNameForFileName()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Package!.IntuneWin!.SetupFile = "sub/dir/setup.exe";

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual("sub\\dir\\setup.exe", payload.SetupFilePath);
        Assert.AreEqual("setup.intunewin", payload.FileName);
    }

    [TestMethod]
    public void Map_UnknownMinimumOsVersion_Throws()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Requirements!.MinimumOSVersion = "10.0.99999";

        Assert.ThrowsExactly<UnsupportedWindowsBuildException>(
            () => Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null));
    }

    [TestMethod]
    public void Map_DetectionScriptContent_IsBase64Encoded()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.HasCount(1, payload.Rules);
        var rule = payload.Rules[0] as Win32LobAppPowerShellScriptRulePayload;
        Assert.IsNotNull(rule);
        Assert.AreEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes(DetectionScript)), rule.ScriptContent);
        Assert.AreEqual("detection", rule.RuleType);
    }

    [TestMethod]
    public void Map_DetectionFlagsOmitted_DefaultToFalse()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Detection!.EnforceSignatureCheck = null;
        app.Detection!.RunAs32Bit = null;

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);
        var rule = payload.Rules[0] as Win32LobAppPowerShellScriptRulePayload;
        Assert.IsNotNull(rule);

        Assert.IsFalse(rule.EnforceSignatureCheck);
        Assert.IsFalse(rule.RunAs32Bit);
    }

    [TestMethod]
    public void Map_DetectionFlagsSet_ArePropagated()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];
        app.Detection!.EnforceSignatureCheck = true;
        app.Detection!.RunAs32Bit = true;

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);
        var rule = payload.Rules[0] as Win32LobAppPowerShellScriptRulePayload;
        Assert.IsNotNull(rule);

        Assert.IsTrue(rule.EnforceSignatureCheck);
        Assert.IsTrue(rule.RunAs32Bit);
    }

    [TestMethod]
    public void Map_OptionalAppInfo_IsCopiedWhenPresent()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Owner = "IT Department";
        manifest.Developer = "Contoso Ltd.";
        manifest.InformationUrl = "https://example.com/info";
        manifest.RoleScopeTagIds = ["0", "1"];
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.AreEqual("IT Department", payload.Owner);
        Assert.AreEqual("Contoso Ltd.", payload.Developer);
        Assert.AreEqual("https://example.com/info", payload.InformationUrl);
        CollectionAssert.AreEqual(new[] { "0", "1" }, payload.RoleScopeTagIds);
    }

    [TestMethod]
    public void Map_RoleScopeTagIdsEmptyList_IsNull()
    {
        var manifest = TestManifests.CreateValid();
        manifest.RoleScopeTagIds = [];
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.IsNull(payload.RoleScopeTagIds);
    }

    [TestMethod]
    public void Map_OptionalAppInfoAbsent_IsNullOrEmpty()
    {
        var manifest = TestManifests.CreateValid();
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: null);

        Assert.IsNull(payload.Owner);
        Assert.IsNull(payload.Developer);
        Assert.IsNull(payload.InformationUrl);
        Assert.IsNull(payload.LargeIcon);
        Assert.IsNull(payload.RoleScopeTagIds);
    }

    [TestMethod]
    public void Map_IconBytesSuppliedWithPngExtension_BuildsLargeIcon()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        var app = manifest.Apps[0];
        byte[] iconBytes = [1, 2, 3, 4];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes);

        Assert.IsNotNull(payload.LargeIcon);
        Assert.AreEqual("image/png", payload.LargeIcon!.Type);
        Assert.AreEqual(Convert.ToBase64String(iconBytes), payload.LargeIcon!.Value);
    }

    [TestMethod]
    [DataRow("assets/icons/contoso-tool.jpg", "image/jpeg")]
    [DataRow("assets/icons/contoso-tool.jpeg", "image/jpeg")]
    public void Map_IconBytesSuppliedWithJpegExtension_BuildsLargeIcon(string iconPath, string expectedMimeType)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = iconPath;
        var app = manifest.Apps[0];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: [1, 2, 3]);

        Assert.AreEqual(expectedMimeType, payload.LargeIcon!.Type);
    }

    [TestMethod]
    public void Map_IconBytesSuppliedWithUnsupportedExtension_Throws()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.bmp";
        var app = manifest.Apps[0];

        Assert.ThrowsExactly<UnsupportedIconFormatException>(
            () => Win32LobAppPayloadMapper.Map(manifest, app, DetectionScript, iconBytes: [1, 2, 3]));
    }

    [TestMethod]
    public void Map_FileVersionDetection_MapsGraphFileSystemRule()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, detectionScriptContent: null, iconBytes: null);

        var rule = payload.Rules[0] as Win32LobAppFileSystemRulePayload;
        Assert.IsNotNull(rule);
        Assert.AreEqual(@"C:\Program Files\Contoso Tool", rule.Path);
        Assert.AreEqual("contoso-tool.exe", rule.FileOrFolderName);
        Assert.AreEqual("version", rule.OperationType);
        Assert.AreEqual("greaterThanOrEqual", rule.Operator);
        Assert.AreEqual("1.2.3", rule.ComparisonValue);
        Assert.IsFalse(rule.Check32BitOn64System);
    }

    [TestMethod]
    public void Map_FileExistsDetection_SynthesizesNotConfiguredOperator()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        app.Detection!.OperationType = "exists";
        app.Detection.Operator = null;
        app.Detection.ComparisonValue = null;
        manifest.Apps = [app];

        var payload = Win32LobAppPayloadMapper.Map(manifest, app, detectionScriptContent: null, iconBytes: null);

        var rule = payload.Rules[0] as Win32LobAppFileSystemRulePayload;
        Assert.IsNotNull(rule);
        Assert.AreEqual("notConfigured", rule.Operator);
        Assert.IsNull(rule.ComparisonValue);
    }

    [TestMethod]
    public void Map_SerializesPolymorphicRulesWithDiscriminatorFirst()
    {
        var scriptRule = new Win32LobAppPowerShellScriptRulePayload
        {
            EnforceSignatureCheck = false,
            RunAs32Bit = false,
            ScriptContent = "ZXhpdCAw",
        };
        var fileRule = new Win32LobAppFileSystemRulePayload
        {
            Path = @"C:\Program Files\Contoso Tool",
            FileOrFolderName = "contoso-tool.exe",
            Check32BitOn64System = false,
            OperationType = "exists",
            Operator = "notConfigured",
        };

        var json = JsonSerializer.Serialize<List<Win32LobAppRulePayload>>([scriptRule, fileRule]);
        using var document = JsonDocument.Parse(json);
        var rules = document.RootElement;

        Assert.AreEqual("@odata.type", rules[0].EnumerateObject().First().Name);
        Assert.AreEqual("#microsoft.graph.win32LobAppPowerShellScriptRule", rules[0].GetProperty("@odata.type").GetString());
        Assert.IsTrue(rules[0].TryGetProperty("scriptContent", out _));
        Assert.AreEqual("@odata.type", rules[1].EnumerateObject().First().Name);
        Assert.AreEqual("#microsoft.graph.win32LobAppFileSystemRule", rules[1].GetProperty("@odata.type").GetString());
        Assert.IsTrue(rules[1].TryGetProperty("path", out _));
        Assert.IsFalse(rules[1].TryGetProperty("scriptContent", out _));
    }

    [TestMethod]
    public void Map_ScriptDetectionWithoutContent_Throws()
    {
        var manifest = TestManifests.CreateValid();

        Assert.ThrowsExactly<InvalidOperationException>(
            () => Win32LobAppPayloadMapper.Map(manifest, manifest.Apps[0], detectionScriptContent: null, iconBytes: null));
    }
}
