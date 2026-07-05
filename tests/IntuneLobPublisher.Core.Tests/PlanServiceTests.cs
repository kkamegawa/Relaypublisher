using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Planning;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class PlanServiceTests
{
    private DirectoryInfo _repoRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _repoRoot = Directory.CreateTempSubdirectory("plan-tests-");
        WriteManifest("manifests/tool-a.yaml", "Contoso.ToolA", "scripts/windows/a/install.ps1");
        WriteManifest("manifests/tool-b.yaml", "Contoso.ToolB", "scripts/windows/b/install.ps1");
    }

    [TestCleanup]
    public void Cleanup() => _repoRoot.Delete(recursive: true);

    private void WriteManifest(string relativePath, string packageIdentifier, string installScript)
    {
        var yaml =
            $"""
             SchemaVersion: "1.0"
             PackageIdentifier: {packageIdentifier}
             PackageName: {packageIdentifier}
             Publisher: Contoso Ltd.
             Description: Sample
             PackageVersion: 1.0.0
             Apps:
               - Platform: windows
                 Architecture: x64
                 InstallerType: win32
                 DisplayName: {packageIdentifier} [Windows x64]
                 Package:
                   IntuneWin:
                     SetupFile: install.ps1
                   RepositoryFiles:
                     - Source: {installScript}
                       Destination: install.ps1
                 Detection:
                   Type: script
                   ScriptFile: scripts/windows/common/detect.ps1
             """;
        var fullPath = Path.Combine(_repoRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, yaml);
    }

    private sealed class FakeGitDiffRunner(IReadOnlyList<string>? changedFiles) : IGitDiffRunner
    {
        public Task<IReadOnlyList<string>?> GetChangedFilesAsync(
            string repositoryRoot, string baseRef, CancellationToken cancellationToken)
            => Task.FromResult(changedFiles);
    }

    private PlanService CreateService(IReadOnlyList<string>? changedFiles)
        => new(new ManifestLoader(), new FakeGitDiffRunner(changedFiles), NullLogger<PlanService>.Instance);

    private Task<IReadOnlyList<string>> ResolveAsync(
        IReadOnlyList<string>? changedFiles,
        string? baseRef = "abc123",
        IReadOnlyList<string>? explicitManifests = null)
        => CreateService(changedFiles).ResolveTargetsAsync(
            new PlanOptions(_repoRoot.FullName, "manifests", baseRef, explicitManifests),
            CancellationToken.None);

    [TestMethod]
    public async Task ResolveTargets_NoBaseRef_SelectsAllManifests()
    {
        var targets = await ResolveAsync(changedFiles: null, baseRef: null);
        CollectionAssert.AreEqual(
            new[] { "manifests/tool-a.yaml", "manifests/tool-b.yaml" }, targets.ToList());
    }

    [TestMethod]
    public async Task ResolveTargets_UnresolvableBaseRef_FallsBackToAllManifests()
    {
        var targets = await ResolveAsync(changedFiles: null);
        Assert.HasCount(2, targets);
    }

    [TestMethod]
    public async Task ResolveTargets_ChangedManifest_SelectsOnlyThatManifest()
    {
        var targets = await ResolveAsync(["manifests/tool-a.yaml"]);
        CollectionAssert.AreEqual(new[] { "manifests/tool-a.yaml" }, targets.ToList());
    }

    [TestMethod]
    public async Task ResolveTargets_ChangedScript_SelectsReferencingManifest()
    {
        var targets = await ResolveAsync(["scripts/windows/b/install.ps1"]);
        CollectionAssert.AreEqual(new[] { "manifests/tool-b.yaml" }, targets.ToList());
    }

    [TestMethod]
    public async Task ResolveTargets_ChangedSharedDetectionScript_SelectsAllReferencingManifests()
    {
        var targets = await ResolveAsync(["scripts/windows/common/detect.ps1"]);
        Assert.HasCount(2, targets);
    }

    [TestMethod]
    public async Task ResolveTargets_UnrelatedChange_SelectsNothing()
    {
        var targets = await ResolveAsync(["doc/readme.md"]);
        Assert.IsEmpty(targets);
    }

    [TestMethod]
    public async Task ResolveTargets_ExplicitManifests_OverrideDiffDetection()
    {
        var targets = await ResolveAsync(
            ["manifests/tool-a.yaml"],
            explicitManifests: ["manifests/tool-b.yaml"]);
        CollectionAssert.AreEqual(new[] { "manifests/tool-b.yaml" }, targets.ToList());
    }

    [TestMethod]
    public async Task ResolveTargets_ExplicitManifestOutsideRepoRoot_Throws()
    {
        await Assert.ThrowsExactlyAsync<UnsafePathException>(
            () => ResolveAsync(changedFiles: null, explicitManifests: ["../outside.yaml"]));
    }

    [TestMethod]
    public void ManifestFileResolver_RoundTripsManifestList()
    {
        var listPath = Path.Combine(_repoRoot.FullName, "manifest-list.json");
        File.WriteAllText(listPath, JsonSerializer.Serialize(new { manifests = new[] { "manifests/tool-a.yaml" } }));

        var resolved = ManifestFileResolver.ReadManifestList(_repoRoot.FullName, listPath);

        Assert.HasCount(1, resolved);
        Assert.IsTrue(File.Exists(resolved[0]));
    }

    [TestMethod]
    public void ManifestFileResolver_ExpandsGlobPatterns()
    {
        var resolved = ManifestFileResolver.ResolvePatterns(_repoRoot.FullName, ["manifests/**/*.yaml"]);
        Assert.HasCount(2, resolved);
    }
}
