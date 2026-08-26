using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

/// <summary>
/// Layer 3 preflight (issue #116): every entry must be independently re-verified before the batch's
/// first Graph write, and one entry's failure must never stop the rest of the batch from being checked
/// (only from being *published* - that boundary is enforced by <c>PublishCommand</c>, not here).
/// </summary>
[TestClass]
public sealed class PublishPreflightTests
{
    private string _packageDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _packageDirectory = Directory.CreateTempSubdirectory("publish-preflight-").FullName;
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_packageDirectory, recursive: true);

    private static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FakeInspector(IReadOnlyList<PkgBundleIdentity>? bundles = null) : IPkgBundleInspector
    {
        private readonly IReadOnlyList<PkgBundleIdentity> _bundles =
            bundles ?? [new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo")];

        public Task<PkgBundleInspectionResult> InspectAsync(Stream pkg, CancellationToken cancellationToken)
            => Task.FromResult(new PkgBundleInspectionResult("1", _bundles));
    }

    private async Task<(IntunePackageManifest Manifest, AppManifest App)> PackageMacOsEntryAsync(
        string packageIdentifier, string content, IPkgBundleInspector inspector, string cliVersion = "1.0.0-test")
    {
        var app = TestManifests.CreateValidMacOsApp();
        app.Source!.Sha256 = HashContent(content);
        var manifest = new IntunePackageManifest
        {
            SchemaVersion = "1.0",
            PackageIdentifier = packageIdentifier,
            PackageName = packageIdentifier,
            Publisher = "Contoso Ltd.",
            Description = "test",
            PackageVersion = "1.0.0",
            Apps = [app],
        };

        var appDirectory = Path.Combine(_packageDirectory, packageIdentifier, "macos-arm64");
        var stagingDirectory = Path.Combine(appDirectory, "staging");
        Directory.CreateDirectory(stagingDirectory);
        var fileName = $"{packageIdentifier}.pkg";
        File.WriteAllText(Path.Combine(stagingDirectory, fileName), content);
        var stagingResult = new MacOsStagingResult(
            packageIdentifier, "macos", "arm64", stagingDirectory, fileName,
            DryRun: false, SummaryPath: Path.Combine(appDirectory, "staging-summary.json"),
            ExpectedSha256: HashContent(content), ActualSha256: HashContent(content));

        var packager = new MacOsPackager(NullLogger<MacOsPackager>.Instance, inspector);
        await packager.CreatePackageAsync(manifest, stagingResult, CancellationToken.None, cliVersion: cliVersion);

        return (manifest, app);
    }

    private void CreateWindowsEntry(string packageIdentifier)
    {
        var entryDirectory = Path.Combine(_packageDirectory, packageIdentifier, "windows-x64");
        Directory.CreateDirectory(entryDirectory);
        var metadata = new PackageMetadata(
            packageIdentifier, "1.0.0", "windows", "x64", "hash-1",
            new PackageToolMetadata("IntuneWinAppUtil.exe", "1.8.6", "toolsha"),
            "install.intunewin", "packagesha", DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(entryDirectory, PackageMetadataJson.FileName),
            JsonSerializer.Serialize(metadata, PackageMetadataJson.SerializerOptions));
        File.WriteAllBytes(Path.Combine(entryDirectory, "install.intunewin"), "windows-payload"u8.ToArray());
    }

    [TestMethod]
    public async Task RunAsync_AllEntriesValid_ReturnsEveryEntryAndNoFailures()
    {
        var inspector = new FakeInspector();
        var (macManifest, macApp) = await PackageMacOsEntryAsync("Contoso.Mac", "mac-content", inspector);
        CreateWindowsEntry("Contoso.Win");
        var winManifest = TestManifests.CreateValid("x64", "Contoso.Win");

        var items = new List<PreflightItem>
        {
            new(macManifest, macApp, new AppIdentity("Contoso.Mac", "macos", "arm64"), "Contoso.Mac macos-arm64", "manifests/mac.yaml"),
            new(winManifest, winManifest.Apps[0], new AppIdentity("Contoso.Win", "windows", "x64"), "Contoso.Win windows-x64", "manifests/win.yaml"),
        };
        var preflight = new PublishPreflight(inspector, NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "1.0.0-test", CancellationToken.None);

        Assert.HasCount(2, result.Entries);
        Assert.IsEmpty(result.Failures);
    }

    [TestMethod]
    public async Task RunAsync_TamperedMacOsArtifact_FailsThatEntryButContinuesOthers()
    {
        var inspector = new FakeInspector();
        var (macManifest, macApp) = await PackageMacOsEntryAsync("Contoso.Mac", "mac-content", inspector);
        CreateWindowsEntry("Contoso.Win");
        var winManifest = TestManifests.CreateValid("x64", "Contoso.Win");

        var contentPath = Path.Combine(_packageDirectory, "Contoso.Mac", "macos-arm64", "staging", "Contoso.Mac.pkg");
        await File.AppendAllTextAsync(contentPath, "tampered");

        var items = new List<PreflightItem>
        {
            new(macManifest, macApp, new AppIdentity("Contoso.Mac", "macos", "arm64"), "Contoso.Mac macos-arm64", "manifests/mac.yaml"),
            new(winManifest, winManifest.Apps[0], new AppIdentity("Contoso.Win", "windows", "x64"), "Contoso.Win windows-x64", "manifests/win.yaml"),
        };
        var preflight = new PublishPreflight(inspector, NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "1.0.0-test", CancellationToken.None);

        Assert.HasCount(1, result.Failures);
        Assert.AreEqual("Contoso.Mac macos-arm64", result.Failures[0].Item.EntryLabel);
        Assert.HasCount(1, result.Entries, "The tampered entry must not block the windows entry's preflight.");
        Assert.AreEqual("Contoso.Win windows-x64", result.Entries[0].Item.EntryLabel);
    }

    [TestMethod]
    public async Task RunAsync_CliVersionMismatch_FailsClosed()
    {
        var inspector = new FakeInspector();
        var (manifest, app) = await PackageMacOsEntryAsync("Contoso.Mac", "content", inspector, cliVersion: "1.0.0");
        var items = new List<PreflightItem>
        {
            new(manifest, app, new AppIdentity("Contoso.Mac", "macos", "arm64"), "label", "path"),
        };
        var preflight = new PublishPreflight(inspector, NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "2.0.0", CancellationToken.None);

        Assert.HasCount(1, result.Failures);
        StringAssert.Contains(result.Failures[0].Message, "CLI");
    }

    [TestMethod]
    public async Task RunAsync_MultipleBundlesWithoutPrimary_SurfacesWarningWithoutFailing()
    {
        var packageInspector = new FakeInspector(
        [
            new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo"),
            new PkgBundleIdentity("com.contoso.helper", "1.0", null, "PackageInfo"),
        ]);
        var (manifest, app) = await PackageMacOsEntryAsync("Contoso.Mac", "content", packageInspector);
        var items = new List<PreflightItem>
        {
            new(manifest, app, new AppIdentity("Contoso.Mac", "macos", "arm64"), "label", "path"),
        };
        var preflight = new PublishPreflight(packageInspector, NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "1.0.0-test", CancellationToken.None);

        Assert.IsEmpty(result.Failures);
        Assert.HasCount(1, result.Entries);
        Assert.IsTrue(result.Entries[0].Warnings.Any(
            w => w.Code == PkgInspectionWarningCode.MultipleBundlesWithoutExplicitPrimary));
    }

    [TestMethod]
    public async Task RunAsync_WindowsEntry_DoesNotHashOrFailOnChangedContent()
    {
        // Windows' recorded content SHA256 is not deterministic (a random per-run encryption key), so
        // preflight must not treat a changed .intunewin byte as tampering the way it does for macOS.
        CreateWindowsEntry("Contoso.Win");
        await File.AppendAllTextAsync(
            Path.Combine(_packageDirectory, "Contoso.Win", "windows-x64", "install.intunewin"), "changed");
        var winManifest = TestManifests.CreateValid("x64", "Contoso.Win");
        var items = new List<PreflightItem>
        {
            new(winManifest, winManifest.Apps[0], new AppIdentity("Contoso.Win", "windows", "x64"), "label", "path"),
        };
        var preflight = new PublishPreflight(new FakeInspector(), NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "1.0.0-test", CancellationToken.None);

        Assert.IsEmpty(result.Failures);
        Assert.HasCount(1, result.Entries);
    }

    [TestMethod]
    public async Task RunAsync_MissingPackageArtifact_FailsThatEntryOnly()
    {
        var winManifest = TestManifests.CreateValid("x64", "Contoso.Missing");
        var items = new List<PreflightItem>
        {
            new(winManifest, winManifest.Apps[0], new AppIdentity("Contoso.Missing", "windows", "x64"), "label", "path"),
        };
        var preflight = new PublishPreflight(new FakeInspector(), NullLogger<PublishPreflight>.Instance);

        var result = await preflight.RunAsync(items, _packageDirectory, "1.0.0-test", CancellationToken.None);

        Assert.HasCount(1, result.Failures);
        Assert.IsEmpty(result.Entries);
    }
}
