using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class InputHashCalculatorTests
{
    private DirectoryInfo _workspace = null!;
    private string _stagingDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("inputhash-tests-");
        _stagingDirectory = Path.Combine(_workspace.FullName, "staging");
        WriteStagedFile("install.ps1", "Write-Host 'install'");
        WriteStagedFile("bin/contoso-tool.exe", "binary-content");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private void WriteStagedFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_stagingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_SameInput_ProducesSameHash()
    {
        var first = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);
        var second = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Length);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_FileContentChange_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        WriteStagedFile("install.ps1", "Write-Host 'changed'");
        var after = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_ManifestChange_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        var changed = TestManifests.CreateValid();
        changed.PackageVersion = "9.9.9";
        var after = await InputHashCalculator.ComputeInputHashAsync(
            changed, _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_AddedFile_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        WriteStagedFile("extra.txt", "extra");
        var after = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void ComputeManifestHash_IsStableForEqualManifests()
    {
        Assert.AreEqual(
            InputHashCalculator.ComputeManifestHash(TestManifests.CreateValid()),
            InputHashCalculator.ComputeManifestHash(TestManifests.CreateValid()));
    }

    /// <summary>
    /// Pinned so that adding an optional manifest field can never silently re-hash - and therefore
    /// re-package and re-upload - every existing manifest in a repository (issue #99). `Categories` is
    /// nullable with no initializer specifically so the canonical JSON of a manifest that does not
    /// declare it stays byte-identical.
    /// </summary>
    private const string PinnedManifestHashWithoutCategories =
        "96016e43c0f78ced7e4a46c5c1699377a31045eec1be183124fc6e2b6e205edc";

    private const string PinnedInputHashWithoutCategories =
        "379fc955db9c86bb41719a6a1cc930eb8ea50b8c52914d649c53c9a609fd452d";

    private const string PinnedMacOsManifestHashWithoutPrimaryOrBuildVersion =
        "da1a7db42a48516926d78942e5f352e6e60532431b60e521904aa4ea25a59a33";

    [TestMethod]
    public void ComputeManifestHash_ManifestWithoutCategories_MatchesThePinnedValue()
    {
        Assert.AreEqual(
            PinnedManifestHashWithoutCategories,
            InputHashCalculator.ComputeManifestHash(TestManifests.CreateValid()));
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_ManifestWithoutCategories_MatchesThePinnedValue()
    {
        Assert.AreEqual(
            PinnedInputHashWithoutCategories,
            await InputHashCalculator.ComputeInputHashAsync(
                TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None));
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_AddingCategories_ChangesTheHash()
    {
        // A category-only manifest change still moves the manifest-wide inputHash, so content may be
        // re-packaged and re-uploaded. That contract is deliberately unchanged (doc/00-overview.md §6.7).
        var withCategories = TestManifests.CreateValid();
        withCategories.Apps[0].Categories = ["Business Apps"];

        var after = await InputHashCalculator.ComputeInputHashAsync(
            withCategories, _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(PinnedInputHashWithoutCategories, after);
    }

    [TestMethod]
    public void ComputeManifestHash_MacOsOptionalPrimaryFieldsOmitted_MatchesPinnedValue()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp()];

        Assert.AreEqual(
            PinnedMacOsManifestHashWithoutPrimaryOrBuildVersion,
            InputHashCalculator.ComputeManifestHash(manifest));
    }

    [TestMethod]
    public void ComputeManifestHash_AddingMacOsPrimaryBundleId_ChangesHash()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.PrimaryBundleId = "com.contoso.tool";
        manifest.Apps = [app];

        Assert.AreNotEqual(
            PinnedMacOsManifestHashWithoutPrimaryOrBuildVersion,
            InputHashCalculator.ComputeManifestHash(manifest));
    }

    [TestMethod]
    public void ComputeManifestHash_AddingMacOsBundleBuildVersion_ChangesHash()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Detection!.IncludedApps![0].BundleBuildVersion = "1234";
        manifest.Apps = [app];

        Assert.AreNotEqual(
            PinnedMacOsManifestHashWithoutPrimaryOrBuildVersion,
            InputHashCalculator.ComputeManifestHash(manifest));
    }

    [TestMethod]
    public void ComputeManifestHash_EmptyCategoriesDiffersFromOmitted()
    {
        var withEmptyCategories = TestManifests.CreateValid();
        withEmptyCategories.Apps[0].Categories = [];

        Assert.AreNotEqual(
            PinnedManifestHashWithoutCategories,
            InputHashCalculator.ComputeManifestHash(withEmptyCategories),
            "`Categories: []` is a real instruction (remove everything), so it must be a hash input.");
    }

    [TestMethod]
    public void ComputeManifestHash_CategoryOrderChange_ChangesTheHash()
    {
        var first = TestManifests.CreateValid();
        first.Apps[0].Categories = ["Business Apps", "Productivity"];
        var second = TestManifests.CreateValid();
        second.Apps[0].Categories = ["Productivity", "Business Apps"];

        Assert.AreNotEqual(
            InputHashCalculator.ComputeManifestHash(first),
            InputHashCalculator.ComputeManifestHash(second));
    }
}
