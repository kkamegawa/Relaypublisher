using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Publishing.Categories;
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

    /// <summary>Fake <see cref="IPlatformAppPublisher"/> that records calls instead of touching Graph.</summary>
    private sealed class FakePlatformAppPublisher : IPlatformAppPublisher
    {
        private readonly List<string> _log;

        public FakePlatformAppPublisher(List<string>? log = null) => _log = log ?? [];

        public List<string> AppCalls { get; } = [];

        public List<(string AppId, string? StoredInputHash)> ContentCalls { get; } = [];

        public Exception? ContentException { get; set; }

        public int EnsureMappableCallCount { get; private set; }

        public string? CreatedNotes { get; private set; }

        public string CreatedAppIdToReturn { get; set; } = CreatedAppId;

        public Task EnsureMappableAsync(PublishRequest request, CancellationToken cancellationToken)
        {
            EnsureMappableCallCount++;
            return Task.CompletedTask;
        }

        public Task<string> CreateAppAsync(PublishRequest request, string notes, CancellationToken cancellationToken)
        {
            AppCalls.Add("create");
            _log.Add("app create");
            CreatedNotes = notes;
            return Task.FromResult(CreatedAppIdToReturn);
        }

        public Task UpdateAppAsync(string appId, PublishRequest request, ContentUploadOptions options, CancellationToken cancellationToken)
        {
            AppCalls.Add($"update {appId}");
            _log.Add($"app update {appId}");
            return Task.CompletedTask;
        }

        public Task<ContentUploadResult> PublishContentAsync(
            string appId, PublishRequest request, PackageArtifacts artifacts, string? storedInputHash,
            ManagementMetadata metadata, ContentUploadOptions options, CancellationToken cancellationToken)
        {
            ContentCalls.Add((appId, storedInputHash));
            _log.Add($"content {appId}");
            if (ContentException is not null)
            {
                return Task.FromException<ContentUploadResult>(ContentException);
            }

            return Task.FromResult(new ContentUploadResult(ContentUploadOutcome.Uploaded, "cv-1"));
        }
    }

    private sealed class FakeAssignmentService : IAssignmentService
    {
        private readonly List<string> _log;

        public FakeAssignmentService(List<string>? log = null) => _log = log ?? [];

        public List<string> Calls { get; } = [];

        public Task<AssignmentPlan> CreatePlanAsync(
            string appId, AppManifest app, AssignmentSyncMode mode, CancellationToken cancellationToken)
        {
            Calls.Add($"plan {appId}");
            _log.Add($"assignment plan {appId}");
            return Task.FromResult(AssignmentPlanner.CreatePlan(appId, app, mode, []));
        }

        public Task ApplyAsync(AssignmentPlan plan, AppManifest app, CancellationToken cancellationToken)
        {
            Calls.Add($"apply {plan.AppId}");
            _log.Add($"assignment apply {plan.AppId}");
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake <see cref="ICategoryService"/>: records the plan/apply calls and honors the
    /// omitted-Categories contract so "no manifest categories means no category work" is observable.
    /// </summary>
    private sealed class FakeCategoryService : ICategoryService
    {
        private readonly List<string> _log;

        public FakeCategoryService(List<string>? log = null) => _log = log ?? [];

        public List<string> Calls { get; } = [];

        /// <summary>Tenant categories the fake resolves manifest names against, by display name.</summary>
        public Dictionary<string, string> TenantCategoryIds { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Business Apps"] = "cat-business",
            ["Productivity"] = "cat-productivity",
        };

        /// <summary>Categories the existing app is already related to.</summary>
        public List<IntuneAppCategory> CurrentCategories { get; } = [];

        public Exception? PlanException { get; set; }

        public Exception? ApplyException { get; set; }

        public Task<CategoryPlan> CreatePlanAsync(string? existingAppId, AppManifest app, CancellationToken cancellationToken)
        {
            Calls.Add($"plan {existingAppId ?? PublishOrchestrator.NewAppPlaceholderId}");
            _log.Add($"category plan {existingAppId ?? PublishOrchestrator.NewAppPlaceholderId}");
            if (PlanException is not null)
            {
                throw PlanException;
            }

            if (app.Categories is null)
            {
                return Task.FromResult(CategoryPlan.NotRequested(existingAppId ?? PublishOrchestrator.NewAppPlaceholderId));
            }

            var desired = app.Categories
                .Select(name => new IntuneAppCategory(TenantCategoryIds[name], name))
                .ToList();
            var current = existingAppId is null ? [] : CurrentCategories;
            return Task.FromResult(CategoryPlanner.CreatePlan(
                existingAppId ?? PublishOrchestrator.NewAppPlaceholderId, desired, current));
        }

        public Task ApplyAsync(CategoryPlan plan, AppManifest app, CancellationToken cancellationToken)
        {
            Calls.Add($"apply {plan.AppId}");
            _log.Add($"category apply {plan.AppId}");
            return ApplyException is null ? Task.CompletedTask : Task.FromException(ApplyException);
        }
    }

    private sealed record Harness(
        PublishOrchestrator Orchestrator,
        FakePlatformAppPublisher Publisher,
        FakeCategoryService CategoryService,
        FakeAssignmentService AssignmentService,
        List<string> Log);

    private static Harness CreateHarness(
        IntuneAppSummary[]? existingApps = null, IReadOnlyDictionary<string, IPlatformAppPublisher>? extraPublishers = null)
    {
        existingApps ??= [];
        var log = new List<string>();
        var publisher = new FakePlatformAppPublisher(log);
        var categoryService = new FakeCategoryService(log);
        var assignmentService = new FakeAssignmentService(log);
        var platformPublishers = new Dictionary<string, IPlatformAppPublisher>(StringComparer.Ordinal) { ["windows"] = publisher };
        if (extraPublishers is not null)
        {
            foreach (var (key, value) in extraPublishers)
            {
                platformPublishers[key] = value;
            }
        }

        var orchestrator = new PublishOrchestrator(
            new IntuneAppResolver(new FakeAppDirectory(existingApps)),
            platformPublishers,
            categoryService,
            assignmentService,
            NullLogger<PublishOrchestrator>.Instance);
        return new Harness(orchestrator, publisher, categoryService, assignmentService, log);
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

    /// <summary>Builds the plan-report object; both callbacks are optional.</summary>
    private static PublishReport Report(
        Action<AssignmentPlan>? assignments = null, Action<CategoryPlan>? categories = null)
        => new() { ReportAssignmentPlan = assignments, ReportCategoryPlan = categories };

    [TestMethod]
    public async Task PublishAsync_NewApp_CreatesThenUploadsThenPlansThenApplies()
    {
        var harness = CreateHarness();
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(), Report(reported.Add), CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.AreEqual(CreatedAppId, result.AppId);
        Assert.IsTrue(result.AppCreated);
        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.ContentOutcome);
        CollectionAssert.AreEqual(new[] { "create" }, harness.Publisher.AppCalls);
        Assert.AreEqual((CreatedAppId, (string?)null), harness.Publisher.ContentCalls.Single());
        CollectionAssert.AreEqual(
            new[] { $"plan {CreatedAppId}", $"apply {CreatedAppId}" }, harness.AssignmentService.Calls);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(0, harness.Publisher.EnsureMappableCallCount, "A real (non-dry-run) publish should not need the separate mapping-validation pass.");
    }

    [TestMethod]
    public async Task PublishAsync_NewApp_CreatePayloadCarriesMetadataNotes()
    {
        var harness = CreateHarness();

        await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        var notes = harness.Publisher.CreatedNotes;
        Assert.IsNotNull(notes);
        Assert.IsTrue(ManagementMetadata.TryParse(notes, out var metadata));
        Assert.AreEqual("Contoso.Tool", metadata!.PackageIdentifier);
        Assert.AreEqual("hash-1", metadata.InputHash);
        Assert.AreEqual("commit-1", metadata.SourceCommit);
    }

    [TestMethod]
    public async Task PublishAsync_ExistingApp_UpdatesAndPassesStoredHash()
    {
        var harness = CreateHarness([ExistingManagedApp(inputHash: "hash-old")]);

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.AreEqual(ExistingAppId, result.AppId);
        Assert.IsFalse(result.AppCreated);
        CollectionAssert.AreEqual(new[] { $"update {ExistingAppId}" }, harness.Publisher.AppCalls);
        Assert.AreEqual((ExistingAppId, (string?)"hash-old"), harness.Publisher.ContentCalls.Single());
    }

    [TestMethod]
    public async Task PublishAsync_ReporterRunsBeforeApply()
    {
        var harness = CreateHarness();
        var applyCallsWhenReported = -1;

        await harness.Orchestrator.PublishAsync(
            CreateRequest(),
            Report(_ => applyCallsWhenReported = harness.AssignmentService.Calls.Count(c => c.StartsWith("apply"))),
            CancellationToken.None);

        Assert.AreEqual(0, applyCallsWhenReported, "The plan must be reported before ApplyAsync runs.");
    }

    [TestMethod]
    public async Task PublishAsync_Downgrade_SkipsWithoutAnyWrite()
    {
        var harness = CreateHarness([ExistingManagedApp(packageVersion: "2.0.0")]);

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.SkippedDowngrade, result.Outcome);
        Assert.IsNotNull(result.SkipReason);
        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.ContentCalls);
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_AllowDowngrade_Publishes()
    {
        var harness = CreateHarness([ExistingManagedApp(packageVersion: "2.0.0")]);

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(allowDowngrade: true), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
    }

    [TestMethod]
    [DataRow("PackageIdentifier")]
    [DataRow("PackageVersion")]
    [DataRow("Platform")]
    [DataRow("Architecture")]
    [DataRow("DisplayName")]
    public async Task PublishAsync_MissingRequiredValue_ThrowsManifestLoadException(string fieldName)
    {
        var manifest = TestManifests.CreateValid();
        switch (fieldName)
        {
            case "PackageIdentifier":
                manifest.PackageIdentifier = null;
                break;
            case "PackageVersion":
                manifest.PackageVersion = null;
                break;
            case "Platform":
                manifest.Apps[0].Platform = null;
                break;
            case "Architecture":
                manifest.Apps[0].Architecture = null;
                break;
            case "DisplayName":
                manifest.Apps[0].DisplayName = null;
                break;
        }

        var harness = CreateHarness();

        var exception = await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => harness.Orchestrator.PublishAsync(CreateRequest(manifest), null, CancellationToken.None));

        StringAssert.Contains(exception.Message, fieldName);
        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.ContentCalls);
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_UnsupportedPlatform_SkipsWithoutAnyCall()
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Platform = "linux";
        var harness = CreateHarness();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(manifest), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.SkippedPlatformNotSupported, result.Outcome);
        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.ContentCalls);
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_MacOsEntry_DispatchesToMacOsPublisher()
    {
        // Confirms the orchestrator's platform dispatch reaches whichever IPlatformAppPublisher is
        // registered for "macos", not just the "windows" one used by every other test in this file.
        var macEntryDirectory = Path.Combine(_packageDirectory, "Contoso.Tool", "macos-arm64");
        Directory.CreateDirectory(macEntryDirectory);
        var macMetadata = new PackageMetadata(
            "Contoso.Tool", "1.2.3", "macos", "arm64", "mac-hash-1",
            Tool: null, IntuneWinFile: null, IntuneWinSha256: null, DateTimeOffset.Parse("2026-07-06T00:00:00Z"),
            ContentFile: "staging/contoso-tool-arm64.pkg", ContentSha256: "pkgsha");
        File.WriteAllText(
            Path.Combine(macEntryDirectory, PackageMetadataJson.FileName),
            JsonSerializer.Serialize(macMetadata, PackageMetadataJson.SerializerOptions));
        Directory.CreateDirectory(Path.Combine(macEntryDirectory, "staging"));
        File.WriteAllBytes(Path.Combine(macEntryDirectory, "staging", "contoso-tool-arm64.pkg"), [1, 2, 3]);

        var macPublisher = new FakePlatformAppPublisher();
        var harness = CreateHarness(extraPublishers: new Dictionary<string, IPlatformAppPublisher> { ["macos"] = macPublisher });
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp()];

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(manifest), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        CollectionAssert.AreEqual(new[] { "create" }, macPublisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.AppCalls);
    }

    [TestMethod]
    public async Task PublishAsync_DryRunExistingApp_MakesNoWriteCallsButValidatesMapping()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(dryRun: true), Report(reported.Add), CancellationToken.None);

        Assert.AreEqual(PublishOutcome.DryRunCompleted, result.Outcome);
        Assert.AreEqual(ExistingAppId, result.AppId);
        Assert.IsNotNull(result.AssignmentPlan);
        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.ContentCalls);
        CollectionAssert.AreEqual(new[] { $"plan {ExistingAppId}" }, harness.AssignmentService.Calls);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual(1, harness.Publisher.EnsureMappableCallCount, "Dry-run should map the payload once to surface mapping errors.");
    }

    [TestMethod]
    public async Task PublishAsync_DryRunNewApp_ReportsAllAddPlan()
    {
        var harness = CreateHarness();
        var reported = new List<AssignmentPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(dryRun: true), Report(reported.Add), CancellationToken.None);

        Assert.AreEqual(PublishOutcome.DryRunCompleted, result.Outcome);
        Assert.IsNull(result.AppId);
        Assert.IsEmpty(harness.AssignmentService.Calls);
        var plan = reported.Single();
        Assert.AreEqual(PublishOrchestrator.NewAppPlaceholderId, plan.AppId);
        Assert.IsTrue(plan.Entries.All(e => e.Action == AssignmentPlanAction.Add));
    }

    [TestMethod]
    public async Task PublishAsync_AdoptedApp_UploadsContentWithNullStoredHash()
    {
        // DisplayName match without metadata: resolution.Metadata is null, so content upload always
        // runs and its notes refresh performs the adopt write-back.
        var harness = CreateHarness([new IntuneAppSummary(ExistingAppId, "Contoso Tool [Windows x64]", null)]);

        var result = await harness.Orchestrator.PublishAsync(CreateRequest(), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.IsFalse(result.AppCreated);
        Assert.AreEqual((ExistingAppId, (string?)null), harness.Publisher.ContentCalls.Single());
    }

    private static IntunePackageManifest ManifestWithCategories(params string[] categories)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps[0].Categories = [.. categories];
        return manifest;
    }

    [TestMethod]
    public async Task PublishAsync_ExistingAppWithCategories_PublishesContentBeforeMetadataAndCategory()
    {
        // Category preflight remains before any write. Existing apps publish content first so Graph
        // accepts the subsequent metadata and category writes only after publishingState is published.
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.CategoryService.CurrentCategories.Add(new IntuneAppCategory("cat-old", "Legacy"));

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        CollectionAssert.AreEqual(
            new[]
            {
                $"category plan {ExistingAppId}",
                $"content {ExistingAppId}",
                $"app update {ExistingAppId}",
                $"category apply {ExistingAppId}",
                $"assignment plan {ExistingAppId}",
                $"assignment apply {ExistingAppId}",
            },
            harness.Log);
    }

    [TestMethod]
    public async Task PublishAsync_NewAppWithCategories_ResolvesBeforeCreateThenAppliesWithTheRealAppId()
    {
        var harness = CreateHarness();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps", "Productivity")), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        CollectionAssert.AreEqual(
            new[]
            {
                $"category plan {PublishOrchestrator.NewAppPlaceholderId}",
                "app create",
                $"content {CreatedAppId}",
                $"category apply {CreatedAppId}",
                $"assignment plan {CreatedAppId}",
                $"assignment apply {CreatedAppId}",
            },
            harness.Log);

        // A new app has no relationships, so everything the manifest wants is an add.
        Assert.IsNotNull(result.CategoryPlan);
        Assert.AreEqual(CreatedAppId, result.CategoryPlan.AppId);
        Assert.IsTrue(result.CategoryPlan.Entries.All(e => e.Action == CategoryPlanAction.Add));
    }

    [TestMethod]
    public async Task PublishAsync_CategoriesOmitted_ReportsNotRequestedPlanAndAppliesNothing()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        var reportedCategoryPlans = new List<CategoryPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(), Report(categories: reportedCategoryPlans.Add), CancellationToken.None);

        Assert.AreEqual(PublishOutcome.Published, result.Outcome);
        Assert.IsNotNull(result.CategoryPlan);
        Assert.IsFalse(result.CategoryPlan.Requested);
        Assert.IsEmpty(result.CategoryPlan.Entries);
        Assert.IsEmpty(reportedCategoryPlans, "A manifest without Categories has no plan worth printing.");
    }

    [TestMethod]
    public async Task PublishAsync_CategoryPreflightFails_MakesNoAppWrite()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.CategoryService.PlanException = new CategorySyncException("Category 'Missing' does not exist in the tenant.");

        await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => harness.Orchestrator.PublishAsync(
                CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None));

        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.Publisher.ContentCalls);
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_CategoryApplyFails_AfterContentAndDoesNotTouchAssignments()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.CategoryService.ApplyException = new CategorySyncException("Failed to add category.");

        await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => harness.Orchestrator.PublishAsync(
                CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None));

        Assert.HasCount(1, harness.Publisher.ContentCalls);
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_ContentFails_DoesNotUpdateMetadataApplyCategoriesOrAssignments()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.Publisher.ContentException = new ContentUploadTimedOutException("publishingState", TimeSpan.FromMinutes(1));

        await Assert.ThrowsExactlyAsync<ContentUploadTimedOutException>(
            () => harness.Orchestrator.PublishAsync(
                CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { $"category plan {ExistingAppId}", $"content {ExistingAppId}" }, harness.Log);
        Assert.IsEmpty(harness.Publisher.AppCalls);
        Assert.IsEmpty(harness.CategoryService.Calls.Where(c => c.StartsWith("apply")));
        Assert.IsEmpty(harness.AssignmentService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_DowngradeSkip_MakesNoCategoryCall()
    {
        var harness = CreateHarness([ExistingManagedApp(packageVersion: "2.0.0")]);

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None);

        Assert.AreEqual(PublishOutcome.SkippedDowngrade, result.Outcome);
        Assert.IsNull(result.CategoryPlan);
        Assert.IsEmpty(harness.CategoryService.Calls);
    }

    [TestMethod]
    public async Task PublishAsync_DryRunWithCategories_ReportsPlanWithoutApplying()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.CategoryService.CurrentCategories.Add(new IntuneAppCategory("cat-old", "Legacy"));
        var reportedCategoryPlans = new List<CategoryPlan>();

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps"), dryRun: true),
            Report(categories: reportedCategoryPlans.Add),
            CancellationToken.None);

        Assert.AreEqual(PublishOutcome.DryRunCompleted, result.Outcome);
        CollectionAssert.AreEqual(new[] { $"plan {ExistingAppId}" }, harness.CategoryService.Calls);
        var plan = reportedCategoryPlans.Single();
        CollectionAssert.AreEqual(
            new[] { CategoryPlanAction.Add, CategoryPlanAction.Remove },
            plan.Entries.Select(e => e.Action).ToList());
    }

    [TestMethod]
    public async Task PublishAsync_DryRunNewAppWithCategories_UsesTheNewAppPlaceholder()
    {
        var harness = CreateHarness();
        var reportedCategoryPlans = new List<CategoryPlan>();

        await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps"), dryRun: true),
            Report(categories: reportedCategoryPlans.Add),
            CancellationToken.None);

        var plan = reportedCategoryPlans.Single();
        Assert.AreEqual(PublishOrchestrator.NewAppPlaceholderId, plan.AppId);
        Assert.AreEqual(CategoryPlanAction.Add, plan.Entries.Single().Action);
        Assert.IsEmpty(harness.CategoryService.Calls.Where(c => c.StartsWith("apply")));
    }

    [TestMethod]
    public async Task PublishAsync_SecondRunWithSameCategories_ConvergesOnKeep()
    {
        var harness = CreateHarness([ExistingManagedApp()]);
        harness.CategoryService.CurrentCategories.Add(new IntuneAppCategory("cat-business", "Business Apps"));

        var result = await harness.Orchestrator.PublishAsync(
            CreateRequest(ManifestWithCategories("Business Apps")), null, CancellationToken.None);

        Assert.IsNotNull(result.CategoryPlan);
        Assert.IsFalse(result.CategoryPlan.HasChanges);
        Assert.AreEqual(CategoryPlanAction.Keep, result.CategoryPlan.Entries.Single().Action);
    }
}
