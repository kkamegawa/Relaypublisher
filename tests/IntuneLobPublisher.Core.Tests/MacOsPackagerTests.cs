using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class MacOsPackagerTests
{
    private DirectoryInfo _workspace = null!;
    private string _outputDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("macos-packager-tests-");
        _outputDirectory = _workspace.FullName;
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private static MacOsPackager CreatePackager() => new(NullLogger<MacOsPackager>.Instance);

    private MacOsStagingResult StageFile(string content = "fake-pkg-binary")
    {
        var appDirectory = Path.Combine(_outputDirectory, "Contoso.Tool", "macos-arm64");
        var stagingDirectory = Path.Combine(appDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "contoso-tool-arm64.pkg"), content);

        return new MacOsStagingResult(
            "Contoso.Tool", "macos", "arm64", stagingDirectory, "contoso-tool-arm64.pkg",
            DryRun: false, SummaryPath: Path.Combine(appDirectory, "staging-summary.json"),
            ExpectedSha256: null, ActualSha256: null);
    }

    [TestMethod]
    public async Task CreatePackageAsync_WritesPackageMetadataWithContentFields()
    {
        var stagingResult = StageFile();

        var result = await CreatePackager().CreatePackageAsync(
            TestManifests.CreateValid(), stagingResult, CancellationToken.None);

        Assert.IsTrue(File.Exists(result.MetadataPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetadataPath));
        var root = document.RootElement;
        Assert.AreEqual("staging/contoso-tool-arm64.pkg", root.GetProperty("contentFile").GetString());
        Assert.AreEqual(result.ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.IsFalse(root.TryGetProperty("intuneWinFile", out _), "IntuneWinFile must not be written for macOS packages.");
        Assert.IsFalse(root.TryGetProperty("tool", out _), "Tool must not be written for macOS packages (no external tool).");
    }

    [TestMethod]
    public async Task CreatePackageAsync_ResultReferencesStagedFile()
    {
        var stagingResult = StageFile();

        var result = await CreatePackager().CreatePackageAsync(
            TestManifests.CreateValid(), stagingResult, CancellationToken.None);

        Assert.IsTrue(File.Exists(result.ContentPath));
        Assert.AreEqual("Contoso.Tool", result.PackageIdentifier);
        Assert.AreEqual("macos", result.Platform);
        Assert.AreEqual("arm64", result.Architecture);
    }

    [TestMethod]
    public async Task CreatePackageAsync_DryRunStagingResult_Throws()
    {
        var stagingResult = StageFile() with { DryRun = true };

        await Assert.ThrowsExactlyAsync<PackagingException>(
            () => CreatePackager().CreatePackageAsync(TestManifests.CreateValid(), stagingResult, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePackageAsync_MissingStagedFile_Throws()
    {
        var stagingResult = StageFile();
        File.Delete(Path.Combine(stagingResult.StagingDirectory, stagingResult.ContentFile));

        await Assert.ThrowsExactlyAsync<PackagingException>(
            () => CreatePackager().CreatePackageAsync(TestManifests.CreateValid(), stagingResult, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePackageAsync_MetadataReadableViaPackageMetadataReader()
    {
        var stagingResult = StageFile();
        var packaged = await CreatePackager().CreatePackageAsync(
            TestManifests.CreateValid(), stagingResult, CancellationToken.None);

        var identity = new AppIdentity("Contoso.Tool", "macos", "arm64");
        var artifacts = await PackageMetadataReader.ReadAsync(_outputDirectory, identity, CancellationToken.None);

        Assert.AreEqual(packaged.ContentPath, artifacts.ContentPath);
        Assert.AreEqual(packaged.InputHash, artifacts.Metadata.InputHash);
        Assert.AreEqual(packaged.ContentSha256, artifacts.Metadata.ContentSha256);
    }
}
