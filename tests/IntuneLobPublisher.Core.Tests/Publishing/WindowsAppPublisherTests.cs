using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
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

    private sealed class ThrowingContentOrchestrator : IMobileAppContentUploadOrchestrator
    {
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
            => throw new NotSupportedException("Not exercised by these tests.");

        public Task WaitWhilePublishingStateProcessingAsync(
            string appId, ContentUploadOptions options, bool useBeta, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ThrowingContentExtractor : IUploadableContentExtractor
    {
        public IUploadableContent Extract(string contentPath) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private WindowsAppPublisher CreatePublisher(out FakeWin32LobAppClient client)
    {
        client = new FakeWin32LobAppClient();
        return new WindowsAppPublisher(client, new ThrowingContentOrchestrator(), new ThrowingContentExtractor());
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
    public async Task EnsureMappableAsync_ScriptDetectionWithMissingFile_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.EnsureMappableAsync(CreateRequest(manifest.Apps[0], manifest), CancellationToken.None));
    }
}
