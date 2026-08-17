using System.Text;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class ManifestAssetValidatorTests
{
    private DirectoryInfo _repoRoot = null!;

    [TestInitialize]
    public void Initialize() => _repoRoot = Directory.CreateTempSubdirectory("manifest-asset-tests-");

    [TestCleanup]
    public void Cleanup() => _repoRoot.Delete(recursive: true);

    private void WriteIcon(string relativePath, int sizeBytes)
    {
        var fullPath = Path.Combine(_repoRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, new byte[sizeBytes]);
    }

    private void WriteScript(string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(_repoRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }

    private void WriteScript(string relativePath, string content, bool withBom = false)
    {
        // Encoding.GetBytes() never emits the preamble regardless of encoderShouldEmitUTF8Identifier
        // (only stream-writing APIs consult GetPreamble()), so the BOM is prepended explicitly here.
        var bytes = Encoding.UTF8.GetBytes(content);
        if (withBom)
        {
            bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
        }

        WriteScript(relativePath, bytes);
    }

    [TestMethod]
    public void Validate_NoIcon_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = null;

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_ExistingIconWithinSizeLimit_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        WriteIcon(manifest.Icon, sizeBytes: 1024);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_MissingIcon_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/missing.png";

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "does not exist");
    }

    [TestMethod]
    public void Validate_IconExceedsMaxSize_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        WriteIcon(manifest.Icon, sizeBytes: (int)ManifestValues.MaxIconBytes + 1);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "exceeds the maximum");
    }

    [TestMethod]
    public void Validate_IconAtExactMaxSize_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "assets/icons/contoso-tool.png";
        WriteIcon(manifest.Icon, sizeBytes: (int)ManifestValues.MaxIconBytes);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_IconEscapesRepository_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Icon = "../outside/icon.png";

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
    }

    [TestMethod]
    public void Validate_NoScripts_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp()];

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_ValidScripts_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest
        {
            PreInstall = "scripts/macos/contoso-tool/preinstall.sh",
            PostInstall = "scripts/macos/contoso-tool/postinstall.sh",
        };
        manifest.Apps = [macApp];
        WriteScript(macApp.Scripts.PreInstall, "#!/bin/bash\necho pre\n");
        WriteScript(macApp.Scripts.PostInstall, "#!/bin/bash\necho post\n");

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_MissingScript_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "does not exist");
    }

    [TestMethod]
    public void Validate_ScriptAtCharacterLimit_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        // "#!/bin/bash\n" then padding so the total length is exactly the (excluded) limit.
        var body = "#!/bin/bash\n" + new string('#', ManifestValues.MaxMacOsAppScriptChars - "#!/bin/bash\n".Length);
        WriteScript(macApp.Scripts.PreInstall, body);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "meets or exceeds the maximum");
    }

    [TestMethod]
    public void Validate_ScriptUnderCharacterLimit_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        var body = "#!/bin/bash\n" + new string('#', ManifestValues.MaxMacOsAppScriptChars - "#!/bin/bash\n".Length - 1);
        WriteScript(macApp.Scripts.PreInstall, body);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_CrlfScriptUnderNormalizedCharacterLimit_ReturnsNoErrors()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];

        var normalized = "#!/bin/bash\n" + new string('#', ManifestValues.MaxMacOsAppScriptChars - "#!/bin/bash\n".Length - 1);
        WriteScript(macApp.Scripts.PreInstall, normalized.Replace("\n", "\r\n", StringComparison.Ordinal));

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_ScriptWithUtf8Bom_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];
        WriteScript(macApp.Scripts.PreInstall, "#!/bin/bash\necho pre\n", withBom: true);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "byte order mark");
    }

    [TestMethod]
    public void Validate_ScriptWithoutShebang_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];
        WriteScript(macApp.Scripts.PreInstall, "echo pre\n");

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "shebang");
    }

    [TestMethod]
    public void Validate_ScriptWithInvalidUtf8_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];
        WriteScript(macApp.Scripts.PreInstall, [0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E, 0x2F, 0x62, 0x61, 0x73, 0x68, 0x0A, 0xFF]);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "valid UTF-8");
    }

    [TestMethod]
    public void Validate_ScriptFileTooLargeForCharacterLimit_ReturnsError()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/contoso-tool/preinstall.sh" };
        manifest.Apps = [macApp];
        WriteScript(
            macApp.Scripts.PreInstall,
            new byte[(int)((long)ManifestValues.MaxMacOsAppScriptChars * 4 + 4)]);

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "too large");
    }

    [TestMethod]
    public void Validate_BothScriptsInvalid_ReturnsErrorsForEach()
    {
        var manifest = TestManifests.CreateValid();
        var macApp = TestManifests.CreateValidMacOsApp();
        macApp.Scripts = new MacOsScriptsManifest
        {
            PreInstall = "scripts/macos/contoso-tool/preinstall.sh",
            PostInstall = "scripts/macos/contoso-tool/postinstall.sh",
        };
        manifest.Apps = [macApp];
        WriteScript(macApp.Scripts.PreInstall, "echo pre\n");
        WriteScript(macApp.Scripts.PostInstall, "echo post\n");

        var errors = ManifestAssetValidator.Validate(manifest, _repoRoot.FullName);

        Assert.HasCount(2, errors);
    }
}
