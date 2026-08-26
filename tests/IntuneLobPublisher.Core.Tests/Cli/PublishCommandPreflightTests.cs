using System.Security.Cryptography;
using System.Text;
using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// Proves the issue #116 zero-Graph-write contract at the CLI layer: <see cref="PublishCommand.RunPreflightAsync"/>
/// is the gate <c>publish</c> runs before it ever constructs a Graph client. An aborted gate returns an
/// empty <see cref="PublishCommand.PreflightGateResult.Entries"/> list, which is itself the proof that
/// nothing downstream (composition, orchestrator, Graph) is reachable - there is nothing left to iterate.
/// </summary>
[TestClass]
public sealed class PublishCommandPreflightTests
{
    private string _packageDirectory = null!;
    private string _repoRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _packageDirectory = Directory.CreateTempSubdirectory("publish-cmd-preflight-pkg-").FullName;
        _repoRoot = Directory.CreateTempSubdirectory("publish-cmd-preflight-repo-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_packageDirectory, recursive: true);
        Directory.Delete(_repoRoot, recursive: true);
    }

    private static string HashContent(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FakeInspector(IReadOnlyList<PkgBundleIdentity>? bundles = null) : IPkgBundleInspector
    {
        private readonly IReadOnlyList<PkgBundleIdentity> _bundles =
            bundles ?? [new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo")];

        public Task<PkgBundleInspectionResult> InspectAsync(Stream pkg, CancellationToken cancellationToken)
            => Task.FromResult(new PkgBundleInspectionResult("1", _bundles));
    }

    private async Task<PublishCommand.PublishEntry> CreateMacOsEntryAsync(
        string packageIdentifier, string content, IPkgBundleInspector inspector)
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
        await packager.CreatePackageAsync(manifest, stagingResult, CancellationToken.None, cliVersion: "1.0.0-test");

        var manifestPath = Path.Combine(_repoRoot, "manifests", $"{packageIdentifier}.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, "# fake manifest for path-only use");

        return new PublishCommand.PublishEntry(
            new IntuneLobPublisher.Core.Validation.LoadedManifest(manifestPath, manifest), app);
    }

    private static Task<PublishCommand.PreflightGateResult> RunAsync(
        List<PublishCommand.PublishEntry> entries,
        string packageDirectory,
        string repoRoot,
        IPkgBundleInspector inspector,
        bool force = false,
        bool interactive = false,
        bool confirm = false)
        => PublishCommand.RunPreflightAsync(
            entries,
            packageDirectory,
            repoRoot,
            inspector,
            NullLogger<PublishPreflight>.Instance,
            "1.0.0-test",
            force,
            () => interactive,
            () => confirm,
            _ => { },
            _ => { },
            CancellationToken.None);

    [TestMethod]
    public async Task RunPreflightAsync_TamperedArtifact_AbortsWithEmptyEntriesAndNoAbortExitCodeIsNull()
    {
        var inspector = new FakeInspector();
        var entry = await CreateMacOsEntryAsync("Contoso.Mac", "content", inspector);
        var contentPath = Path.Combine(_packageDirectory, "Contoso.Mac", "macos-arm64", "staging", "Contoso.Mac.pkg");
        await File.AppendAllTextAsync(contentPath, "tampered");

        var result = await RunAsync([entry], _packageDirectory, _repoRoot, inspector);

        Assert.AreEqual(ExitCodes.Failure, result.AbortExitCode);
        Assert.IsEmpty(result.Entries, "An aborted gate must return no entries: nothing downstream can reach Graph.");
        Assert.IsNotNull(result.AbortResultEntries);
        Assert.HasCount(1, result.AbortResultEntries);
        Assert.AreEqual("failed", result.AbortResultEntries[0].Outcome);
    }

    [TestMethod]
    public async Task RunPreflightAsync_WarningDeclinedInteractively_AbortsWithEmptyEntries()
    {
        var inspector = new FakeInspector(
        [
            new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo"),
            new PkgBundleIdentity("com.contoso.helper", "1.0", null, "PackageInfo"),
        ]);
        var entry = await CreateMacOsEntryAsync("Contoso.Mac", "content", inspector);

        var result = await RunAsync([entry], _packageDirectory, _repoRoot, inspector, interactive: true, confirm: false);

        Assert.AreEqual(ExitCodes.Failure, result.AbortExitCode);
        Assert.IsEmpty(result.Entries);
        Assert.IsNotNull(result.AbortResultEntries);
        Assert.HasCount(1, result.AbortResultEntries);
        CollectionAssert.Contains(result.AbortResultEntries[0].WarningCodes, "MultipleBundlesWithoutExplicitPrimary");
        Assert.IsNull(result.WarningsAcknowledgedViaForce);
    }

    [TestMethod]
    public async Task RunPreflightAsync_WarningWithoutForceOffTty_AbortsForceRequired()
    {
        var inspector = new FakeInspector(
        [
            new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo"),
            new PkgBundleIdentity("com.contoso.helper", "1.0", null, "PackageInfo"),
        ]);
        var entry = await CreateMacOsEntryAsync("Contoso.Mac", "content", inspector);

        var result = await RunAsync([entry], _packageDirectory, _repoRoot, inspector, interactive: false);

        Assert.AreEqual(ExitCodes.Failure, result.AbortExitCode);
        Assert.IsEmpty(result.Entries);
    }

    [TestMethod]
    public async Task RunPreflightAsync_WarningWithForce_SucceedsAndRecordsForceAcknowledged()
    {
        var inspector = new FakeInspector(
        [
            new PkgBundleIdentity("com.contoso.tool", "1.2.3", null, "PackageInfo"),
            new PkgBundleIdentity("com.contoso.helper", "1.0", null, "PackageInfo"),
        ]);
        var entry = await CreateMacOsEntryAsync("Contoso.Mac", "content", inspector);

        var result = await RunAsync([entry], _packageDirectory, _repoRoot, inspector, force: true);

        Assert.IsNull(result.AbortExitCode);
        Assert.HasCount(1, result.Entries);
        Assert.IsNotNull(result.Entries[0].VerifiedArtifacts);
        Assert.IsNotEmpty(result.Entries[0].Warnings!);
        Assert.IsTrue(result.WarningsAcknowledgedViaForce);
    }

    [TestMethod]
    public async Task RunPreflightAsync_NoWarnings_SucceedsWithNullAcknowledgement()
    {
        var inspector = new FakeInspector();
        var entry = await CreateMacOsEntryAsync("Contoso.Mac", "content", inspector);

        var result = await RunAsync([entry], _packageDirectory, _repoRoot, inspector);

        Assert.IsNull(result.AbortExitCode);
        Assert.HasCount(1, result.Entries);
        Assert.IsEmpty(result.Entries[0].Warnings!);
        Assert.IsNull(result.WarningsAcknowledgedViaForce);
    }
}
