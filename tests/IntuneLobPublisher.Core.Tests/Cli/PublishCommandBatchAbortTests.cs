using System.Text.Json;
using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// A per-app failure must not stop the batch (reruns converge, doc/00-overview.md 6.10), but a failure
/// that no other entry can survive must. These pin that distinction, plus issue #150's per-entry Graph
/// session lifecycle and the unified single-write-point result file.
/// </summary>
[TestClass]
public sealed class PublishCommandBatchAbortTests
{
    private string _repoRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _repoRoot = Directory.CreateTempSubdirectory("publish-batch-abort-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_repoRoot, recursive: true);
    }

    private sealed class ThrowingOrchestrator : IPublishOrchestrator
    {
        private readonly Func<Exception> _createException;

        public ThrowingOrchestrator(Func<Exception> createException) => _createException = createException;

        public int CallCount { get; private set; }

        public Task<PublishResult> PublishAsync(
            PublishRequest request, PublishReport? report, CancellationToken cancellationToken)
        {
            CallCount++;
            throw _createException();
        }
    }

    /// <summary>Returns a different, pre-scripted outcome per call, in order - for a batch where entries do not all fail the same way.</summary>
    private sealed class SequencedOrchestrator : IPublishOrchestrator
    {
        private readonly Func<PublishRequest, PublishResult>[] _steps;

        public SequencedOrchestrator(params Func<PublishRequest, PublishResult>[] steps) => _steps = steps;

        public int CallCount { get; private set; }

        public Task<PublishResult> PublishAsync(
            PublishRequest request, PublishReport? report, CancellationToken cancellationToken)
        {
            // Real Graph calls observe the token; this fake does the same so a test can simulate the
            // caller cancelling mid-batch by cancelling a shared CancellationTokenSource inside a step.
            cancellationToken.ThrowIfCancellationRequested();
            var step = _steps[CallCount];
            CallCount++;
            return Task.FromResult(step(request));
        }

        public static PublishResult Published(PublishRequest request)
            => new(PublishOutcome.Published, "app-" + request.Manifest.PackageIdentifier, true, ContentUploadOutcome.Uploaded, null, null);

        public static PublishResult ThrowUploadFailure(PublishRequest request)
            => throw new ContentUploadRejectedException("stageBlock", 403, "AuthenticationFailed", null, "req-id");
    }

    /// <summary>Wraps an <see cref="IPublishOrchestrator"/> as an <see cref="IPublishSession"/> that reports its own disposal.</summary>
    private sealed class StubSession(IPublishOrchestrator orchestrator, Action? onDispose) : IPublishSession
    {
        public IPublishOrchestrator Orchestrator { get; } = orchestrator;

        public void Dispose() => onDispose?.Invoke();
    }

    /// <summary>Counts how many sessions <see cref="PublishCommand.PublishEntriesAsync"/> creates and disposes.</summary>
    private sealed class SessionFactory(IPublishOrchestrator orchestrator)
    {
        public int CreateCount { get; private set; }

        public int DisposeCount { get; private set; }

        /// <summary>When set, thrown from <see cref="Create"/> instead of returning a session - simulates a credential/HttpClient construction failure.</summary>
        public Func<Exception>? ThrowOnCreate { get; set; }

        public IPublishSession Create()
        {
            CreateCount++;
            if (ThrowOnCreate is { } throwOnCreate)
            {
                throw throwOnCreate();
            }

            return new StubSession(orchestrator, () => DisposeCount++);
        }
    }

    private List<PublishCommand.PublishEntry> CreateEntries(int count)
    {
        var manifests = new List<LoadedManifest>();
        for (var i = 0; i < count; i++)
        {
            var identifier = $"Contoso.Tool{(char)('A' + i)}";
            manifests.Add(new LoadedManifest(
                Path.Combine(_repoRoot, "manifests", $"tool-{(char)('a' + i)}.yaml"),
                TestManifests.CreateValid("x64", identifier, $"Tool {(char)('A' + i)} [Windows x64]")));
        }

        var entries = PublishCommand.SelectHighestVersions(manifests);
        Assert.HasCount(count, entries);
        return entries;
    }

    private List<PublishCommand.PublishEntry> CreateTwoEntries() => CreateEntries(2);

    private Task<int> RunAsync(IPublishOrchestrator orchestrator, string resultFile)
        => RunAsync(new SessionFactory(orchestrator), CreateTwoEntries(), resultFile);

    private Task<int> RunAsync(SessionFactory factory, List<PublishCommand.PublishEntry> entries, string resultFile, CancellationToken cancellationToken = default)
        => PublishCommand.PublishEntriesAsync(
            factory.Create,
            entries,
            _repoRoot,
            Path.Combine(_repoRoot, "out"),
            "source-commit",
            allowDowngrade: false,
            dryRun: true,
            resultFile,
            cancellationToken);

    [TestMethod]
    public async Task PublishEntriesAsync_GraphAccessDenied_StopsAfterTheFirstEntry()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(
            () => new GraphAccessDeniedException("Failed to list Intune mobile apps.", 403, null, null, "Forbidden"));

        var exitCode = await RunAsync(orchestrator, resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(1, orchestrator.CallCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
        Assert.AreEqual("failed", document.RootElement[0].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_TenantMismatch_AbortsAfterTheFirstEntry()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(() => new TenantMismatchException("expected-tenant", "actual-tenant"));

        var exitCode = await RunAsync(orchestrator, resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(1, orchestrator.CallCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
        Assert.AreEqual("failed", document.RootElement[0].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_PerAppGraphFailure_ContinuesWithTheRemainingEntries()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(
            () => new GraphRequestException("Graph request to '/beta/...' returned 403.", 403, null, null));

        var exitCode = await RunAsync(orchestrator, resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(2, orchestrator.CallCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(2, document.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_CategorySyncFailure_ContinuesWithTheRemainingEntries()
    {
        // A missing/ambiguous category name or a failed $ref is per-entry: the rest of the batch must
        // still publish, and a rerun converges (issue #99).
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(
            () => new CategorySyncException("Category 'Business Apps' does not exist in the tenant."));

        var exitCode = await RunAsync(orchestrator, resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(2, orchestrator.CallCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(2, document.RootElement.GetArrayLength());
        Assert.AreEqual("failed", document.RootElement[0].GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, document.RootElement[0].GetProperty("categoryOutcome").ValueKind);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_AllEntriesSucceed_CreatesAndDisposesOneSessionPerEntry()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new SequencedOrchestrator(SequencedOrchestrator.Published, SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        var exitCode = await RunAsync(factory, CreateTwoEntries(), resultFile);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(2, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_ResultFileWriteFailsAfterSuccessfulBatch_ReturnsFailureAndDisposesEverySession()
    {
        var orchestrator = new SequencedOrchestrator(SequencedOrchestrator.Published, SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        var exitCode = await RunAsync(factory, CreateTwoEntries(), _repoRoot);

        Assert.AreEqual(ExitCodes.Failure, exitCode);
        Assert.AreEqual(2, orchestrator.CallCount);
        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(2, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_AbortPath_DisposesTheFailingEntrySession()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(
            () => new GraphAccessDeniedException("Failed to list Intune mobile apps.", 403, null, null, "Forbidden"));
        var factory = new SessionFactory(orchestrator);

        await RunAsync(factory, CreateTwoEntries(), resultFile);

        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(1, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_AbortAndResultFileWriteFails_PreservesAbortFailure()
    {
        var orchestrator = new ThrowingOrchestrator(
            () => new GraphAccessDeniedException("Failed to list Intune mobile apps.", 403, null, null, "Forbidden"));
        var factory = new SessionFactory(orchestrator);

        var exitCode = await RunAsync(factory, CreateTwoEntries(), _repoRoot);

        Assert.AreEqual(ExitCodes.Failure, exitCode);
        Assert.AreEqual(1, orchestrator.CallCount);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(1, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_PerAppFailureAndResultFileWriteFails_PreservesPublishFailure()
    {
        var orchestrator = new SequencedOrchestrator(
            SequencedOrchestrator.Published,
            SequencedOrchestrator.ThrowUploadFailure,
            SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        var exitCode = await RunAsync(factory, CreateEntries(3), _repoRoot);

        Assert.AreEqual(ExitCodes.Failure, exitCode);
        Assert.AreEqual(3, orchestrator.CallCount);
        Assert.AreEqual(3, factory.CreateCount);
        Assert.AreEqual(3, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_PerAppFailure_DisposesEverySession()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(
            () => new GraphRequestException("Graph request to '/beta/...' returned 403.", 403, null, null));
        var factory = new SessionFactory(orchestrator);

        await RunAsync(factory, CreateTwoEntries(), resultFile);

        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(2, factory.DisposeCount);
    }

    [TestMethod]
    public async Task PublishEntriesAsync_ThreeEntryBatch_SuccessUploadFailureSuccess_RecordsAllThreeAndFails()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new SequencedOrchestrator(
            SequencedOrchestrator.Published,
            SequencedOrchestrator.ThrowUploadFailure,
            SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        var exitCode = await RunAsync(factory, CreateEntries(3), resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(3, orchestrator.CallCount);
        Assert.AreEqual(3, factory.CreateCount);
        Assert.AreEqual(3, factory.DisposeCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(3, document.RootElement.GetArrayLength());
        Assert.AreEqual("published", document.RootElement[0].GetProperty("outcome").GetString());
        Assert.AreEqual("failed", document.RootElement[1].GetProperty("outcome").GetString());
        Assert.AreEqual("published", document.RootElement[2].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_SessionFactoryThrowsAuthenticationFailed_AbortsAndWritesResultFile()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new SequencedOrchestrator(SequencedOrchestrator.Published, SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator)
        {
            ThrowOnCreate = () => new Azure.Identity.AuthenticationFailedException("credential chain exhausted"),
        };

        var exitCode = await RunAsync(factory, CreateTwoEntries(), resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(0, orchestrator.CallCount);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(0, factory.DisposeCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
        Assert.AreEqual("failed", document.RootElement[0].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_UnexpectedException_RecordsFailureWritesResultFileAndAborts()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        var orchestrator = new ThrowingOrchestrator(() => new InvalidOperationException("unanticipated failure"));

        var exitCode = await RunAsync(orchestrator, resultFile);

        Assert.AreNotEqual(0, exitCode);
        Assert.AreEqual(1, orchestrator.CallCount);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
        Assert.AreEqual("failed", document.RootElement[0].GetProperty("outcome").GetString());
        StringAssert.Contains(document.RootElement[0].GetProperty("skipReason").GetString(), "InvalidOperationException");
    }

    [TestMethod]
    public async Task PublishEntriesAsync_CancelledToken_StillWritesResultFileForCompletedEntries()
    {
        var resultFile = Path.Combine(_repoRoot, "result.json");
        using var cts = new CancellationTokenSource();
        var orchestrator = new SequencedOrchestrator(
            request =>
            {
                // The first entry completes normally, then the caller cancels before the second entry starts.
                cts.Cancel();
                return SequencedOrchestrator.Published(request);
            },
            SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RunAsync(factory, CreateTwoEntries(), resultFile, cts.Token));

        // The cancellation must still let the single result-file write point run (issue #150): the
        // first, completed entry is on record even though the run overall did not finish.
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        Assert.AreEqual(1, document.RootElement.GetArrayLength());
        Assert.AreEqual("published", document.RootElement[0].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task PublishEntriesAsync_CancelledTokenAndResultFileWriteFails_PreservesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var orchestrator = new SequencedOrchestrator(
            request =>
            {
                cts.Cancel();
                return SequencedOrchestrator.Published(request);
            },
            SequencedOrchestrator.Published);
        var factory = new SessionFactory(orchestrator);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => RunAsync(factory, CreateTwoEntries(), _repoRoot, cts.Token));

        Assert.AreEqual(1, orchestrator.CallCount);
        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(2, factory.DisposeCount);
    }
}
