using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class WindowsStagingServiceTests
{
    private static readonly byte[] ExternalContent = "external-binary"u8.ToArray();
    private static readonly string ExternalContentSha256 =
        Convert.ToHexStringLower(SHA256.HashData(ExternalContent));

    private DirectoryInfo _workspace = null!;
    private string _repoRoot = null!;
    private string _outputDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("staging-tests-");
        _repoRoot = Path.Combine(_workspace.FullName, "repo");
        _outputDirectory = Path.Combine(_workspace.FullName, "out");

        WriteRepoFile("scripts/windows/x64/install.ps1", "Write-Host 'install x64'");
        WriteRepoFile("scripts/windows/arm64/install.ps1", "Write-Host 'install arm64'");
        WriteRepoFile("scripts/windows/common/detect.ps1", "Write-Host 'Detected'");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private void WriteRepoFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    /// <summary>In-memory publicHttp stand-in so staging tests need no network.</summary>
    private sealed class FakeSourceProvider : ISourceProvider
    {
        public string SourceType => "publicHttp";

        public async Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, ExternalContent, cancellationToken);
            return new DownloadedFile(request.DestinationPath, ExternalContent.Length, ExternalContentSha256);
        }
    }

    private static WindowsStagingService CreateService()
        => new(
            new SourceProviderRegistry([new FakeSourceProvider()]),
            NullLogger<WindowsStagingService>.Instance);

    private IntunePackageManifest CreateManifest(string architecture = "x64")
    {
        var manifest = TestManifests.CreateValid(architecture);
        manifest.Apps[0].Package!.ExternalFiles[0].Sha256 = ExternalContentSha256;
        return manifest;
    }

    private Task<StagingResult> StageAsync(IntunePackageManifest manifest, bool dryRun = false)
        => CreateService().StageAsync(
            manifest,
            manifest.Apps[0],
            new StagingOptions(_repoRoot, _outputDirectory, dryRun),
            CancellationToken.None);

    [TestMethod]
    public async Task StageAsync_CopiesRepositoryFileAndDownloadsExternalFile()
    {
        var result = await StageAsync(CreateManifest());

        var stagedScript = Path.Combine(result.StagingDirectory, "install.ps1");
        Assert.IsTrue(File.Exists(stagedScript));
        Assert.AreEqual("Write-Host 'install x64'", await File.ReadAllTextAsync(stagedScript));

        var stagedBinary = Path.Combine(result.StagingDirectory, "bin", "contoso-tool.exe");
        Assert.IsTrue(File.Exists(stagedBinary));
        Assert.AreEqual(ExternalContentSha256, result.ExternalFiles[0].ActualSha256);
    }

    [TestMethod]
    public async Task StageAsync_Arm64UsesDifferentInstallScript()
    {
        var manifest = CreateManifest("arm64");
        manifest.Apps[0].Package!.RepositoryFiles[0].Source = "scripts/windows/arm64/install.ps1";

        var result = await StageAsync(manifest);

        var stagedScript = Path.Combine(result.StagingDirectory, "install.ps1");
        Assert.AreEqual("Write-Host 'install arm64'", await File.ReadAllTextAsync(stagedScript));
        Assert.Contains("windows-arm64", result.StagingDirectory);
    }

    [TestMethod]
    public async Task StageAsync_MissingRepositoryFile_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.RepositoryFiles[0].Source = "scripts/windows/x64/missing.ps1";

        await Assert.ThrowsExactlyAsync<StagingException>(() => StageAsync(manifest));
    }

    [TestMethod]
    public async Task StageAsync_SetupFileNotProduced_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.IntuneWin!.SetupFile = "setup.exe";

        await Assert.ThrowsExactlyAsync<StagingException>(() => StageAsync(manifest));
    }

    [TestMethod]
    public async Task StageAsync_WritesStagingSummaryJson()
    {
        var result = await StageAsync(CreateManifest());

        Assert.IsNotNull(result.SummaryPath);
        Assert.IsTrue(File.Exists(result.SummaryPath));

        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(result.SummaryPath!));
        var root = summary.RootElement;
        Assert.AreEqual("Contoso.Tool", root.GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("windows", root.GetProperty("platform").GetString());
        Assert.AreEqual("install.ps1", root.GetProperty("setupFile").GetString());
        Assert.AreEqual(
            ExternalContentSha256,
            root.GetProperty("externalFiles")[0].GetProperty("actualSha256").GetString());
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(result.SummaryPath!));
    }

    [TestMethod]
    public async Task StageAsync_DryRun_PerformsNoFileOperations()
    {
        var result = await StageAsync(CreateManifest(), dryRun: true);

        Assert.IsTrue(result.DryRun);
        Assert.IsNull(result.SummaryPath);
        Assert.IsFalse(Directory.Exists(_outputDirectory));
        Assert.HasCount(1, result.RepositoryFiles);
        Assert.HasCount(1, result.ExternalFiles);
    }

    [TestMethod]
    public async Task StageAsync_Sha256Mismatch_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.ExternalFiles[0].Sha256 = new string('0', 64);

        await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(() => StageAsync(manifest));
    }

    [TestMethod]
    [DataRow("../evil.ps1")]
    [DataRow("..\\evil.ps1")]
    [DataRow("C:\\evil.ps1")]
    [DataRow("/evil.ps1")]
    public async Task StageAsync_UnsafeRepositoryDestination_Throws(string destination)
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.RepositoryFiles[0].Destination = destination;

        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => StageAsync(manifest));
    }

    [TestMethod]
    [DataRow("../evil.ps1")]
    [DataRow("C:\\evil.ps1")]
    public async Task StageAsync_UnsafeSetupFile_Throws(string setupFile)
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.IntuneWin!.SetupFile = setupFile;

        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => StageAsync(manifest));
    }

    [TestMethod]
    public async Task StageAsync_UnsafeExternalDestination_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Package!.ExternalFiles[0].Destination = "../evil.exe";

        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => StageAsync(manifest));
    }

    [TestMethod]
    public async Task StageAsync_MissingDetectionScript_Throws()
    {
        var manifest = CreateManifest();
        manifest.Apps[0].Detection!.ScriptFile = "scripts/windows/common/missing-detect.ps1";

        await Assert.ThrowsExactlyAsync<StagingException>(() => StageAsync(manifest));
    }
}
