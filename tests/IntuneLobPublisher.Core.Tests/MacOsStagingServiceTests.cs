using System.Security.Cryptography;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class MacOsStagingServiceTests
{
    private static readonly byte[] PkgContent = "fake-pkg-binary"u8.ToArray();
    private static readonly string PkgContentSha256 = Convert.ToHexStringLower(SHA256.HashData(PkgContent));

    private DirectoryInfo _workspace = null!;
    private string _repoRoot = null!;
    private string _outputDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("macos-staging-tests-");
        _repoRoot = Path.Combine(_workspace.FullName, "repo");
        _outputDirectory = Path.Combine(_workspace.FullName, "out");
        Directory.CreateDirectory(_repoRoot);
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    /// <summary>In-memory azureBlob stand-in so staging tests need no network.</summary>
    private sealed class FakeSourceProvider : ISourceProvider
    {
        public string SourceType => "azureBlob";

        public async Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, PkgContent, cancellationToken);
            return new DownloadedFile(request.DestinationPath, PkgContent.Length, PkgContentSha256);
        }
    }

    private static MacOsStagingService CreateService()
        => new(
            new SourceProviderRegistry([new FakeSourceProvider()]),
            NullLogger<MacOsStagingService>.Instance);

    private IntunePackageManifest CreateManifest(string? architecture = "arm64")
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp(architecture);
        app.Source!.Sha256 = PkgContentSha256;
        manifest.Apps = [app];
        return manifest;
    }

    private Task<MacOsStagingResult> StageAsync(IntunePackageManifest manifest, bool dryRun = false)
        => CreateService().StageAsync(
            manifest,
            manifest.Apps[0],
            new StagingOptions(_repoRoot, _outputDirectory, dryRun),
            CancellationToken.None);

    [TestMethod]
    public async Task StageAsync_DownloadsSourceAndVerifiesChecksum()
    {
        var manifest = CreateManifest();
        var result = await StageAsync(manifest);

        var stagedPkg = Path.Combine(result.StagingDirectory, manifest.Apps[0].Source!.Destination!);
        Assert.IsTrue(File.Exists(stagedPkg));
        CollectionAssert.AreEqual(PkgContent, await File.ReadAllBytesAsync(stagedPkg));
        Assert.AreEqual(PkgContentSha256, result.ActualSha256);
        Assert.AreEqual(manifest.Apps[0].Source!.Destination, result.ContentFile);
    }

    [TestMethod]
    public async Task StageAsync_OmittedArchitecture_ResolvesToUniversalStagingDirectory()
    {
        // AppArchitecture.Resolve (issue #123): an omitted macOS Architecture stages under
        // "macos-universal", matching what package/publish will key identity off of downstream.
        var manifest = CreateManifest(architecture: null);
        var result = await StageAsync(manifest);

        Assert.AreEqual("universal", result.Architecture);
        Assert.EndsWith(Path.Combine("Contoso.Tool", "macos-universal", "staging"), result.StagingDirectory);
    }

    [TestMethod]
    public async Task StageAsync_WritesStagingSummaryJson()
    {
        var result = await StageAsync(CreateManifest());

        Assert.IsNotNull(result.SummaryPath);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(result.SummaryPath!));
        var root = summary.RootElement;
        Assert.AreEqual("Contoso.Tool", root.GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("macos", root.GetProperty("platform").GetString());
        Assert.AreEqual(PkgContentSha256, root.GetProperty("source").GetProperty("actualSha256").GetString());
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(result.SummaryPath!));
    }

    [TestMethod]
    public async Task StageAsync_DryRun_PerformsNoFileOperations()
    {
        var result = await StageAsync(CreateManifest(), dryRun: true);

        Assert.IsTrue(result.DryRun);
        Assert.IsNull(result.SummaryPath);
        Assert.IsFalse(Directory.Exists(_outputDirectory));
    }

    [TestMethod]
    public async Task StageAsync_Sha256Mismatch_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Source!.Sha256 = new string('0', 64);

        await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(() => StageAsync(manifest));
    }

    [TestMethod]
    [DataRow("../evil.pkg")]
    [DataRow("..\\evil.pkg")]
    [DataRow("C:\\evil.pkg")]
    [DataRow("/evil.pkg")]
    public async Task StageAsync_UnsafeDestination_Throws(string destination)
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Source!.Destination = destination;

        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => StageAsync(manifest));
    }

    [TestMethod]
    public async Task StageAsync_NoSourceDefined_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Source = null;

        await Assert.ThrowsExactlyAsync<StagingException>(() => StageAsync(manifest));
    }
}
