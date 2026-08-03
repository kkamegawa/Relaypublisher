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
}
