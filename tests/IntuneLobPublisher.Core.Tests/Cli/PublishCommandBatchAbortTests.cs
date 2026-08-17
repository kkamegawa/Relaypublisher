using System.Text.Json;
using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// A per-app failure must not stop the batch (reruns converge, doc/00-overview.md 6.10), but a failure
/// that no other entry can survive must. These pin that distinction.
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
            PublishRequest request, Action<AssignmentPlan>? reportAssignmentPlan, CancellationToken cancellationToken)
        {
            CallCount++;
            throw _createException();
        }
    }

    private List<PublishCommand.PublishEntry> CreateTwoEntries()
    {
        var manifests = new[]
        {
            new LoadedManifest(
                Path.Combine(_repoRoot, "manifests", "tool-a.yaml"),
                TestManifests.CreateValid("x64", "Contoso.ToolA", "Tool A [Windows x64]")),
            new LoadedManifest(
                Path.Combine(_repoRoot, "manifests", "tool-b.yaml"),
                TestManifests.CreateValid("x64", "Contoso.ToolB", "Tool B [Windows x64]")),
        };

        var entries = PublishCommand.SelectHighestVersions(manifests);
        Assert.HasCount(2, entries);
        return entries;
    }

    private Task<int> RunAsync(IPublishOrchestrator orchestrator, string resultFile)
        => PublishCommand.PublishEntriesAsync(
            orchestrator,
            CreateTwoEntries(),
            _repoRoot,
            Path.Combine(_repoRoot, "out"),
            "source-commit",
            allowDowngrade: false,
            dryRun: true,
            resultFile,
            CancellationToken.None);

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
}
