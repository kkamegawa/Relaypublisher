using System.Text.Json;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class PublishOrchestratorTests
{
    private const string ExistingAppId = "app-1";
    private const string CreatedAppId = "app-new";

    private string _repoRoot = null!;
    private string _packageDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _repoRoot = Directory.CreateTempSubdirectory("publish-orchestrator-repo-").FullName;
        _packageDirectory = Directory.CreateTempSubdirectory("publish-orchestrator-out-").FullName;

        var scriptDirectory = Path.Combine(_repoRoot, "scripts", "windows", "common");
        Directory.CreateDirectory(scriptDirectory);
        File.WriteAllText(Path.Combine(scriptDirectory, "detect.ps1"), "exit 0");

        var entryDirectory = Path.Combine(_packageDirectory, "Contoso.Tool", "windows-x64");
        Directory.CreateDirectory(entryDirectory);
        var metadata = new PackageMetadata(
            "Contoso.Tool", "1.2.3", "windows", "x64", "hash-1",
            new PackageToolMetadata("IntuneWinAppUtil.exe", "1.8.6", "toolsha"),
            "install.intunewin", "packagesha", DateTimeOffset.Parse("2026-07-06T00:00:00Z"));
        File.WriteAllText(
            Path.Combine(entryDirectory, PackageMetadataJson.FileName),
            JsonSerializer.Serialize(metadata, PackageMetadataJson.SerializerOptions));
        File.WriteAllBytes(Path.Combine(entryDirectory, "install.intunewin"), [1, 2, 3]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_repoRoot, recursive: true);
        Directory.Delete(_packageDirectory, recursive: true);
    }

    private sealed class FakeAppDirectory(params IntuneAppSummary[] apps) : IIntuneAppDirectory
    {
        public Task<IReadOnlyList<IntuneAppSummary>> ListAppsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IntuneAppSummary>>(apps);
    }

    private sealed class FakeAppClient : IWin32LobAppClient
    {
        public List<string> Calls { get; } = [];
        public Win32LobAppPayload? CreatedPayload { get; private set; }
        public Win32LobAppPayload? UpdatedPayload { get; private set; }

        public Task<string> CreateAppAsync(Win32LobAppPayload payload, CancellationToken cancellationToken)
        {
            Calls.Add("create");
            CreatedPayload = payload;
            return Task.FromResult(CreatedAppId);
        }

        public Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken)
        {
            Calls.Add($"update {appId}");
            UpdatedPayload = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeContentOrchestrator : IWin32LobAppContentUploadOrchestrator
    {
        public List<(string AppId, string? StoredInputHash)> Calls { get; } = [];

        public Task<ContentUploadResult> PublishContentAsync(
            string appId, IntuneWinPackageResult package, string? storedInputHash,
            ManagementMetadata metadata, ContentUploadOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((appId, storedInputHash));
            return Task.FromResult(new ContentUploadResult(ContentUploadOutcome.Uploaded, "cv-1"));
        }
    }

    private sealed class FakeAssignmentService : IAssignmentService
    {
        public List<string> Calls { get; } = [];

        public Task<AssignmentPlan> CreatePlanAsync(
            string appId, AppManifest app, AssignmentSyncMode mode, CancellationToken cancellationToken)
        {
            Calls.Add($"plan {appId}");
            return Task.FromResult(AssignmentPlanner.CreatePlan(appId, app, mode, []));
        }

        public Task ApplyAsync(AssignmentPlan plan, AppManifest app, CancellationToken cancellationToken)
        {
            Calls.Add($"apply {plan.AppId}");
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        PublishOrchestrator Orchestrator,
        FakeAppClient AppClient,
        FakeContentOrchestrator ContentOrchestrator,
        FakeAssignmentService AssignmentService);

    private static Harness CreateHarness(params IntuneAppSummary[] existingApps)
    {
        var appClient = new FakeAppClient();
        var contentOrchestrator = new FakeContentOrchestrator();
        var assignmentService = new FakeAssignmentService();
        var orchestrator = new PublishOrchestrator(
            new IntuneAppResolver(new FakeAppDirectory(existingApps)),
            appClient,
            contentOrchestrator,
            assignmentService,
            NullLogger<PublishOrchestrator>.Instance);
        return new Harness(orchestrator, appClient, contentOrchestrator, assignmentService);
    }

    private PublishRequest CreateRequest(
        IntunePackageManifest? manifest = null,
        bool allowDowngrade = false,
        bool dryRun = false)
    {
        manifest ??= TestManifests.CreateValid();
        return new PublishRequest(
            manifest,
            manifest.Apps[0],
            "manifests/contoso-tool.yaml",
            _repoRoot,
            _packageDirectory,
            "commit-1",
            allowDowngrade,
            dryRun);
    }

    private static IntuneAppSummary ExistingManagedApp(
        string packageVersion = "1.0.0", string inputHash = "hash-old")
    {
        var notes = new ManagementMetadata
        {
            PackageIdentifier = "Contoso.Tool",
            PackageVersion = packageVersion,
            Platform = "windows",
            Architecture = "x64",
            ManifestPath = "manifests/contoso-tool.yaml",
            ManifestHash = "mh-old",
            InputHash = inputHash,
            SourceCommit = "commit-0",
        }.Serialize();
        return new IntuneAppSummary(ExistingAppId, "Contoso Tool [Windows x64]", notes);
    }

    [TestMethod]
    public async Task PublishAsync_NewApp_CreatesThenUploadsThenPlansThenApplies()
    {
        var harness = CreateHarness();
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(), reported.Add, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.AreEqual(CreatedAppId, result.AppId);
        Assert.IsTrue(result.AppCreated);
        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.ContentOutcome);
        CollectionAssert.AreEqual(new[] { "create" }, harness.AppClient.Calls);
        Assert.AreEqual((CreatedAppId, (string?)null), harness.ContentOrchestrator.Calls.Single());
        CollectionAssert.AreEqual(
            new[] { $"plan {CreatedAppId}", $"apply {CreatedAppId}" }, harness.AssignmentService.Calls);
        Assert.AreEqual(1, reported.Count);
    }

    [TestMethod]
    public async Task PublishAsync_NewApp_CreatePayloadCarriesMetadataNotes()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        var notes = harness.AppClient.CreatedPayload!.Notes;
        Assert.IsNotNull(notes);
        Assert.IsTrue(ManagementMetadata.TryParse(notes, out var metadata));
        Assert.AreEqual("Contoso.Tool", metadata!.PackageIdentifier);
        Assert.AreEqual("hash-1", metadata.InputHash);
        Assert.AreEqual("commit-1", metadata.SourceCommit);
    }

    [TestMethod]
    public async Task PublishAsync_ExistingApp_UpdatesWithoutNotesAndPassesStoredHash()
    {
        var harness = CreateHarness(ExistingManagedApp(inputHash: "hash-old"));

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.AreEqual(ExistingAppId, result.AppId);
        Assert.IsFalse(result.AppCreated);
        CollectionAssert.AreEqual(new[] { $"update {ExistingAppId}" }, harness.AppClient.Calls);
        Assert.IsNull(harness.AppClient.UpdatedPayload!.Notes);
        Assert.AreEqual((ExistingAppId, (string?)"hash-old"), harness.ContentOrchestrator.Calls.Single());
    }

    [TestMethod]
    public async Task PublishAsync_ReporterRunsBeforeApply()
    {
        var harness = CreateHarness();
        var applyCallsWhenReported = -1;

        await harness.Orchestrator.PublishAsync(
            CreateRequest(),
            _ => applyCallsWhenReported = harness.AssignmentService.Calls.Count(c => c.StartsWith("apply")),
            CancellationToken.None);

        Assert.AreEqual(0, applyCallsWhenReported, "The plan must be reported before ApplyAsync runs.");
    }

    [TestMethod]
    public async Task PublishAsync_Downgrade_SkipsWithoutAnyWrite()
    {
        var harness = CreateHarness(ExistingManagedApp(packageVersion: "2.0.0"));

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.SkippedDowngrade, result.Outcome);
        Assert.IsNotNull(result.SkipReason);
        Assert.AreEqual(0, harness.AppClient.Calls.Count);
        Assert.AreEqual(0, harness.ContentOrchestrator.Calls.Count);
        Assert.AreEqual(0, harness.AssignmentService.Calls.Count);
    }

    [TestMethod]
    public async Task PublishAsync_AllowDowngrade_Publishes()
    {
        var harness = CreateHarness(ExistingManagedApp(packageVersion: "2.0.0"));

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(allowDowngrade: true), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
    }

    /// <summary>
    /// Exercises the orchestrator's defense-in-depth platform gate directly. A manifest with
    /// Platform "macos" is rejected by AppManifestValidator before it ever reaches
    /// PublishOrchestrator in the CLI pipeline (ManifestValues.Platforms currently allows only
    /// "windows"); this test builds the manifest by hand to confirm the gate still skips safely
    /// for callers that construct a PublishRequest without going through that validator.
    /// </summary>
    [TestMethod]
    public async Task PublishAsync_MacOsEntry_SkipsWithoutAnyCall()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Platform = "macos";
        var harness = CreateHarness();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(manifest), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.SkippedPlatformNotSupported, result.Outcome);
        Assert.AreEqual(0, harness.AppClient.Calls.Count);
        Assert.AreEqual(0, harness.ContentOrchestrator.Calls.Count);
        Assert.AreEqual(0, harness.AssignmentService.Calls.Count);
    }

    [TestMethod]
    public async Task PublishAsync_DryRunExistingApp_MakesNoWriteCalls()
    {
        var harness = CreateHarness(ExistingManagedApp());
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(dryRun: true), reported.Add, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.DryRunCompleted, result.Outcome);
        Assert.AreEqual(ExistingAppId, result.AppId);
        Assert.IsNotNull(result.AssignmentPlan);
        Assert.AreEqual(0, harness.AppClient.Calls.Count);
        Assert.AreEqual(0, harness.ContentOrchestrator.Calls.Count);
        CollectionAssert.AreEqual(new[] { $"plan {ExistingAppId}" }, harness.AssignmentService.Calls);
        Assert.AreEqual(1, reported.Count);
    }

    [TestMethod]
    public async Task PublishAsync_DryRunNewApp_ReportsAllAddPlan()
    {
        var harness = CreateHarness();
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(dryRun: true), reported.Add, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.DryRunCompleted, result.Outcome);
        Assert.IsNull(result.AppId);
        Assert.AreEqual(0, harness.AssignmentService.Calls.Count);
        var plan = reported.Single();
        Assert.AreEqual(PublishOrchestrator.NewAppPlaceholderId, plan.AppId);
        Assert.IsTrue(plan.Entries.All(e => e.Action == AssignmentPlanAction.Add));
    }

    [TestMethod]
    public async Task PublishAsync_AdoptedApp_UploadsContentWithNullStoredHash()
    {
        // DisplayName match without metadata: resolution.Metadata is null, so content upload always
        // runs and its notes refresh performs the adopt write-back.
        var harness = CreateHarness(new IntuneAppSummary(ExistingAppId, "Contoso Tool [Windows x64]", null));

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.IsFalse(result.AppCreated);
        Assert.AreEqual((ExistingAppId, (string?)null), harness.ContentOrchestrator.Calls.Single());
    }
}
