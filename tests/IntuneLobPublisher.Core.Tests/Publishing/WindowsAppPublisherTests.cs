using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class WindowsAppPublisherTests
{
    private DirectoryInfo _repoRoot = null!;

    [TestInitialize]
    public void Initialize() => _repoRoot = Directory.CreateTempSubdirectory("windows-publisher-tests-");

    [TestCleanup]
    public void Cleanup() => _repoRoot.Delete(recursive: true);

    private sealed class FakeWin32LobAppClient : IWin32LobAppClient
    {
        public Win32LobAppPayload? LastPayload { get; private set; }

        public Task<string> CreateAppAsync(Win32LobAppPayload payload, CancellationToken cancellationToken)
        {
            LastPayload = payload;
            return Task.FromResult("app-1");
        }

        public Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken)
        {
            LastPayload = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingContentOrchestrator : IMobileAppContentUploadOrchestrator
    {
        /// <summary>Records the `useBeta` passed to each call so tests can assert win32LobApp always stays on beta.</summary>
        public List<bool> UseBetaCalls { get; } = [];

        public Task<ContentUploadResult> PublishContentAsync(
            string appId,
            PublishableContent content,
            string? storedInputHash,
            ManagementMetadata metadata,
            ContentUploadOptions options,
            IUploadableContentExtractor extractor,
            string oDataType,
            bool useBeta,
            CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            return Task.FromResult(new ContentUploadResult(ContentUploadOutcome.Uploaded, "cv-1"));
        }

        public Task WaitWhilePublishingStateProcessingAsync(
            string appId, ContentUploadOptions options, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingContentExtractor : IUploadableContentExtractor
    {
        public IUploadableContent Extract(string contentPath) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private WindowsAppPublisher CreatePublisher(out FakeWin32LobAppClient client)
        => CreatePublisher(out client, out _);

    private WindowsAppPublisher CreatePublisher(out FakeWin32LobAppClient client, out RecordingContentOrchestrator orchestrator)
    {
        client = new FakeWin32LobAppClient();
        orchestrator = new RecordingContentOrchestrator();
        return new WindowsAppPublisher(client, orchestrator, new ThrowingContentExtractor());
    }

    private PublishRequest CreateRequest(AppManifest app, IntunePackageManifest manifest) => new(
        manifest,
        app,
        "manifests/contoso-tool-windows-x64.yaml",
        _repoRoot.FullName,
        "out",
        "abc123",
        AllowDowngrade: false,
        DryRun: false);

    [TestMethod]
    public async Task EnsureMappableAsync_FileDetection_DoesNotReadDetectionScript()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];
        var publisher = CreatePublisher(out _);

        await publisher.EnsureMappableAsync(CreateRequest(app, manifest), CancellationToken.None);
    }

    [TestMethod]
    public async Task CreateAppAsync_FileDetection_MapsFileRuleWithoutDetectionScript()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];
        var publisher = CreatePublisher(out var client);

        var appId = await publisher.CreateAppAsync(CreateRequest(app, manifest), "{}", CancellationToken.None);

        Assert.AreEqual("app-1", appId);
        Assert.IsInstanceOfType<Win32LobAppFileSystemRulePayload>(client.LastPayload!.Rules[0]);
    }

    [TestMethod]
    public async Task UpdateAppAsync_FileDetection_MapsFileRuleWithoutDetectionScript()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];
        var publisher = CreatePublisher(out var client);

        await publisher.UpdateAppAsync("app-1", CreateRequest(app, manifest), new ContentUploadOptions(), CancellationToken.None);

        Assert.IsInstanceOfType<Win32LobAppFileSystemRulePayload>(client.LastPayload!.Rules[0]);
    }

    [TestMethod]
    public async Task UpdateAppAsync_AlwaysUsesGraphBeta()
    {
        // win32LobApp's displayVersion/roleScopeTagIds only exist on the beta resource
        // (doc/adr/publishing.md 2026-09-10 entry), so the pre-update processing-state guard must
        // check the app on beta too.
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];
        var publisher = CreatePublisher(out _, out var orchestrator);

        await publisher.UpdateAppAsync("app-1", CreateRequest(app, manifest), new ContentUploadOptions(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { true }, orchestrator.UseBetaCalls);
    }

    [TestMethod]
    public async Task PublishContentAsync_AlwaysUsesGraphBeta()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidFileDetectionApp();
        manifest.Apps = [app];
        var publisher = CreatePublisher(out _, out var orchestrator);
        var metadata = new PackageMetadata(
            "contoso-tool", "1.0.0", "windows", "x64", "input-hash", Tool: null,
            IntuneWinFile: "contoso-tool.intunewin", IntuneWinSha256: "hash", GeneratedUtc: DateTimeOffset.UtcNow);
        var artifacts = new PackageArtifacts(metadata, Path.Combine(_repoRoot.FullName, "contoso-tool.intunewin"));

        await publisher.PublishContentAsync(
            "app-1", CreateRequest(app, manifest), artifacts, storedInputHash: null,
            new ManagementMetadata
            {
                PackageIdentifier = "contoso-tool",
                PackageVersion = "1.0.0",
                Platform = "windows",
                Architecture = "x64",
                ManifestPath = "manifests/contoso-tool-windows-x64.yaml",
                ManifestHash = "manifest-hash",
                InputHash = "input-hash",
                SourceCommit = "abc123",
            },
            new ContentUploadOptions(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { true }, orchestrator.UseBetaCalls);
    }

    [TestMethod]
    public async Task EnsureMappableAsync_ScriptDetectionWithMissingFile_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.EnsureMappableAsync(CreateRequest(manifest.Apps[0], manifest), CancellationToken.None));
    }
}
