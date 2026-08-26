using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
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

    private static MacOsPackager CreatePackager(IPkgBundleInspector? inspector = null) => new(
        NullLogger<MacOsPackager>.Instance,
        inspector ?? new TestPkgBundleInspector());

    private static IntunePackageManifest CreateValidMacOsManifest(string content = "fake-pkg-binary")
    {
        var app = TestManifests.CreateValidMacOsApp();
        app.Source!.Sha256 = HashContent(content);
        return new IntunePackageManifest
        {
            SchemaVersion = "1.0",
            PackageIdentifier = "Contoso.Tool",
            PackageName = "Contoso Tool",
            Publisher = "Contoso Ltd.",
            Description = "Internal tool for Contoso employees.",
            PackageVersion = "1.2.3",
            Apps = [app],
        };
    }

    private MacOsStagingResult StageFile(string content = "fake-pkg-binary")
    {
        var appDirectory = Path.Combine(_outputDirectory, "Contoso.Tool", "macos-arm64");
        var stagingDirectory = Path.Combine(appDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "contoso-tool-arm64.pkg"), content);

        return new MacOsStagingResult(
            "Contoso.Tool", "macos", "arm64", stagingDirectory, "contoso-tool-arm64.pkg",
            DryRun: false, SummaryPath: Path.Combine(appDirectory, "staging-summary.json"),
            ExpectedSha256: HashContent(content), ActualSha256: HashContent(content));
    }

    private static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [TestMethod]
    public async Task CreatePackageAsync_WritesPackageMetadataWithContentFields()
    {
        var stagingResult = StageFile();

        var result = await CreatePackager().CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None, cliVersion: "1.2.3-test");

        Assert.IsTrue(File.Exists(result.MetadataPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetadataPath));
        var root = document.RootElement;
        Assert.AreEqual("staging/contoso-tool-arm64.pkg", root.GetProperty("contentFile").GetString());
        Assert.AreEqual(result.ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.AreEqual(result.ContentSize, root.GetProperty("contentSize").GetInt64());
        Assert.AreEqual("1.2.3-test", root.GetProperty("cliVersion").GetString());
        Assert.AreEqual(2, root.GetProperty("metadataSchemaVersion").GetInt32());
        Assert.AreEqual("com.contoso.tool", root.GetProperty("inspection").GetProperty("selectedPrimaryBundleId").GetString());
        Assert.IsFalse(root.TryGetProperty("intuneWinFile", out _), "IntuneWinFile must not be written for macOS packages.");
        Assert.IsFalse(root.TryGetProperty("tool", out _), "Tool must not be written for macOS packages (no external tool).");
    }

    [TestMethod]
    public async Task CreatePackageAsync_ResultReferencesStagedFile()
    {
        var stagingResult = StageFile();

        var result = await CreatePackager().CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None);

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
            () => CreatePackager().CreatePackageAsync(CreateValidMacOsManifest(), stagingResult, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePackageAsync_MissingStagedFile_Throws()
    {
        var stagingResult = StageFile();
        File.Delete(Path.Combine(stagingResult.StagingDirectory, stagingResult.ContentFile));

        await Assert.ThrowsExactlyAsync<PackagingException>(
            () => CreatePackager().CreatePackageAsync(CreateValidMacOsManifest(), stagingResult, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePackageAsync_MetadataReadableViaPackageMetadataReader()
    {
        var stagingResult = StageFile();
        var packaged = await CreatePackager().CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None);

        var identity = new AppIdentity("Contoso.Tool", "macos", "arm64");
        var artifacts = await PackageMetadataReader.ReadAsync(_outputDirectory, identity, CancellationToken.None);

        Assert.AreEqual(packaged.ContentPath, artifacts.ContentPath);
        Assert.AreEqual(packaged.InputHash, artifacts.Metadata.InputHash);
        Assert.AreEqual(packaged.ContentSha256, artifacts.Metadata.ContentSha256);
    }

    [TestMethod]
    public async Task ReadAndVerifyAsync_UnchangedArtifact_RehashesAndReinspectsSuccessfully()
    {
        var stagingResult = StageFile();
        var inspector = new TestPkgBundleInspector();
        var packaged = await CreatePackager(inspector).CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None, cliVersion: "1.2.3-test");

        var artifacts = await PackageMetadataReader.ReadAndVerifyAsync(
            _outputDirectory, new AppIdentity("Contoso.Tool", "macos", "arm64"), inspector, CancellationToken.None);

        Assert.AreEqual(packaged.ContentSha256, artifacts.Metadata.ContentSha256);
        Assert.AreEqual(2, inspector.CallCount, "Package and publish verification must inspect the exact artifact independently.");
    }

    [TestMethod]
    public async Task ReadAndVerifyAsync_TamperedArtifact_FailsBeforeReinspection()
    {
        var stagingResult = StageFile();
        var inspector = new TestPkgBundleInspector();
        var packaged = await CreatePackager(inspector).CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None, cliVersion: "1.2.3-test");
        await File.AppendAllTextAsync(packaged.ContentPath, "tampered");

        await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageMetadataReader.ReadAndVerifyAsync(
            _outputDirectory, new AppIdentity("Contoso.Tool", "macos", "arm64"), inspector, CancellationToken.None));

        Assert.AreEqual(1, inspector.CallCount, "A size/hash failure must stop before the publish-side XAR parser.");
    }

    [TestMethod]
    public async Task ReadAndVerifyAsync_InspectionFactsChanged_FailsClosed()
    {
        var stagingResult = StageFile();
        await CreatePackager().CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None, cliVersion: "1.2.3-test");
        var changedInspector = new TestPkgBundleInspector(
            [new PkgBundleIdentity("com.contoso.other", "9.0", null, "PackageInfo")]);

        await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageMetadataReader.ReadAndVerifyAsync(
            _outputDirectory, new AppIdentity("Contoso.Tool", "macos", "arm64"), changedInspector, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadAndVerifyAsync_ManifestAwareVerification_BindsSourceShaAndCliVersion()
    {
        var stagingResult = StageFile();
        var inspector = new TestPkgBundleInspector();
        var manifest = CreateValidMacOsManifest();
        // forceAcknowledged: true at package time must not leak into the fresh report: a --force
        // acknowledgement covers only the run that gave it, never a later publish's decision.
        var packaged = await CreatePackager(inspector).CreatePackageAsync(
            manifest, stagingResult, CancellationToken.None, forceAcknowledged: true, cliVersion: "1.2.3-test");
        manifest.Apps[0].Source!.Sha256 = packaged.ContentSha256;

        var verification = await PackageMetadataReader.ReadAndVerifyAsync(
            _outputDirectory,
            new AppIdentity("Contoso.Tool", "macos", "arm64"),
            manifest,
            manifest.Apps[0],
            inspector,
            "1.2.3-test",
            CancellationToken.None);

        Assert.AreEqual(packaged.ContentSha256, verification.Artifacts.Metadata.ContentSha256);
        Assert.IsFalse(
            verification.FreshReport.ForceAcknowledged,
            "The fresh report must not inherit the saved metadata's ForceAcknowledged flag.");
    }

    [TestMethod]
    public async Task ReadAndVerifyAsync_ManifestSourceShaChanged_FailsBeforeReinspection()
    {
        var stagingResult = StageFile();
        var inspector = new TestPkgBundleInspector();
        var manifest = CreateValidMacOsManifest();
        await CreatePackager(inspector).CreatePackageAsync(
            manifest, stagingResult, CancellationToken.None, cliVersion: "1.2.3-test");
        manifest.Apps[0].Source!.Sha256 = new string('0', 64);

        await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageMetadataReader.ReadAndVerifyAsync(
            _outputDirectory,
            new AppIdentity("Contoso.Tool", "macos", "arm64"),
            manifest,
            manifest.Apps[0],
            inspector,
            "1.2.3-test",
            CancellationToken.None));

        Assert.AreEqual(1, inspector.CallCount, "A manifest SHA mismatch must stop before publish-side inspection.");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not-a-sha256")]
    public async Task CreatePackageAsync_InvalidManifestSourceSha_FailsBeforeInspection(string? sourceSha256)
    {
        var stagingResult = StageFile();
        var inspector = new TestPkgBundleInspector();
        var manifest = CreateValidMacOsManifest();
        manifest.Apps[0].Source!.Sha256 = sourceSha256;

        await Assert.ThrowsExactlyAsync<PackagingException>(() => CreatePackager(inspector).CreatePackageAsync(
            manifest, stagingResult, CancellationToken.None));

        Assert.AreEqual(0, inspector.CallCount);
    }

    [TestMethod]
    public async Task CreatePackageAsync_ExpectedShaMismatch_DoesNotInvokeInspector()
    {
        var stagingResult = StageFile() with { ExpectedSha256 = new string('0', 64) };
        var inspector = new TestPkgBundleInspector();

        await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(() => CreatePackager(inspector).CreatePackageAsync(
            CreateValidMacOsManifest(), stagingResult, CancellationToken.None));

        Assert.AreEqual(0, inspector.CallCount);
    }

    private sealed class TestPkgBundleInspector : IPkgBundleInspector
    {
        private readonly IReadOnlyList<PkgBundleIdentity> _bundles;

        public TestPkgBundleInspector(IReadOnlyList<PkgBundleIdentity>? bundles = null)
        {
            _bundles = bundles ?? [new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo")];
        }

        public int CallCount { get; private set; }

        public Task<PkgBundleInspectionResult> InspectAsync(Stream pkg, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new PkgBundleInspectionResult(
                XarPkgBundleInspector.CurrentInspectorVersion,
                _bundles));
        }
    }
}
