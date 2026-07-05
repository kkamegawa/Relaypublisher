using System.IO.Compression;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class Win32LobAppContentUploadOrchestratorTests
{
    private DirectoryInfo _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = Directory.CreateTempSubdirectory("content-upload-orchestrator-tests-");

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class FakeMobileAppContentClient : IMobileAppContentClient
    {
        public Queue<MobileAppContentFileResponse> FileResponses { get; } = new();

        public Queue<string> PublishingStates { get; } = new();

        public List<(string Name, long Size, long SizeEncrypted)> CreateContentFileCalls { get; } = [];

        public List<FileEncryptionInfoPayload> CommitFileCalls { get; } = [];

        public int RenewUploadCallCount { get; private set; }

        public string? PatchedCommittedContentVersion { get; private set; }

        public List<string> PatchedNotes { get; } = [];

        public Task<string> CreateContentVersionAsync(string appId, CancellationToken cancellationToken) => Task.FromResult("cv-1");

        public Task<string> CreateContentFileAsync(
            string appId, string contentVersionId, string name, long size, long sizeEncrypted, CancellationToken cancellationToken)
        {
            CreateContentFileCalls.Add((name, size, sizeEncrypted));
            return Task.FromResult("file-1");
        }

        public Task<MobileAppContentFileResponse> GetContentFileAsync(
            string appId, string contentVersionId, string fileId, CancellationToken cancellationToken)
        {
            if (FileResponses.Count == 0)
            {
                throw new InvalidOperationException("No more queued file responses.");
            }

            return Task.FromResult(FileResponses.Dequeue());
        }

        public Task RenewUploadAsync(string appId, string contentVersionId, string fileId, CancellationToken cancellationToken)
        {
            RenewUploadCallCount++;
            return Task.CompletedTask;
        }

        public Task CommitFileAsync(
            string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, CancellationToken cancellationToken)
        {
            CommitFileCalls.Add(fileEncryptionInfo);
            return Task.CompletedTask;
        }

        public Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, CancellationToken cancellationToken)
        {
            PatchedCommittedContentVersion = contentVersionId;
            return Task.CompletedTask;
        }

        public Task PatchNotesAsync(string appId, string notes, CancellationToken cancellationToken)
        {
            PatchedNotes.Add(notes);
            return Task.CompletedTask;
        }

        public Task<string> GetPublishingStateAsync(string appId, CancellationToken cancellationToken)
        {
            if (PublishingStates.Count == 0)
            {
                throw new InvalidOperationException("No more queued publishing states.");
            }

            return Task.FromResult(PublishingStates.Dequeue());
        }
    }

    private sealed class FakeAzureStorageBlockBlobUploader : IAzureStorageBlockBlobUploader
    {
        public bool InvokeRenewal { get; set; }

        public SasUriRenewal? LastRenewal { get; private set; }

        public async Task UploadAsync(
            Uri sasUri, DateTimeOffset expiresAt, Stream content,
            Func<CancellationToken, Task<SasUriRenewal>> renewAsync, ContentUploadOptions options, CancellationToken cancellationToken)
        {
            if (InvokeRenewal)
            {
                LastRenewal = await renewAsync(cancellationToken).ConfigureAwait(false);
            }

            using var drain = new MemoryStream();
            await content.CopyToAsync(drain, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ManagementMetadata CreateMetadata() => new()
    {
        PackageIdentifier = "Contoso.Tool",
        PackageVersion = "1.2.3",
        Platform = "windows",
        Architecture = "x64",
        ManifestPath = "manifests/Contoso/Contoso.Tool/1.2.3/Contoso.Tool.yaml",
        ManifestHash = "manifest-hash",
        InputHash = "input-hash-new",
        SourceCommit = "abc123",
    };

    private static MobileAppContentFileResponse FileState(string uploadState, string? azureStorageUri = null, bool includeExpiration = true)
        => new()
        {
            Id = "file-1",
            UploadState = uploadState,
            AzureStorageUri = azureStorageUri,
            AzureStorageUriExpirationDateTime = azureStorageUri is not null && includeExpiration ? DateTimeOffset.UtcNow.AddHours(1) : null,
        };

    private string CreateIntuneWinFile()
    {
        var path = Path.Combine(_workspace.FullName, $"{Guid.NewGuid()}.intunewin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var metadataEntry = archive.CreateEntry("IntuneWinPackage/Metadata/Detection.xml");
        using (var writer = new StreamWriter(metadataEntry.Open()))
        {
            writer.Write($"""
                <ApplicationInfo ToolVersion="1.8.5.0">
                  <Name>install.ps1</Name>
                  <UnencryptedContentSize>4</UnencryptedContentSize>
                  <FileName>IntunePackage.intunewin</FileName>
                  <SetupFile>install.ps1</SetupFile>
                  <EncryptionInfo>
                    <EncryptionKey>{Convert.ToBase64String([1, 2, 3])}</EncryptionKey>
                    <MacKey>{Convert.ToBase64String(new byte[32])}</MacKey>
                    <InitializationVector>{Convert.ToBase64String(new byte[16])}</InitializationVector>
                    <Mac>{Convert.ToBase64String(new byte[32])}</Mac>
                    <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
                    <FileDigest>{Convert.ToBase64String([4, 5, 6])}</FileDigest>
                    <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
                  </EncryptionInfo>
                </ApplicationInfo>
                """);
        }

        var contentEntry = archive.CreateEntry("IntuneWinPackage/Contents/IntunePackage.intunewin");
        using (var stream = contentEntry.Open())
        {
            stream.Write([1, 2, 3, 4]);
        }

        return path;
    }

    private IntuneWinPackageResult CreatePackage(string inputHash = "input-hash-new")
        => new("Contoso.Tool", "windows", "x64", CreateIntuneWinFile(), "irrelevant-sha256", inputHash, "1.8.5.0", new string('a', 64), "irrelevant-metadata.json");

    private static ContentUploadOptions FastOptions() => new()
    {
        AzureStorageUriPollInterval = TimeSpan.FromSeconds(1),
        AzureStorageUriTimeout = TimeSpan.FromSeconds(3),
        CommitPollInterval = TimeSpan.FromSeconds(1),
        CommitTimeout = TimeSpan.FromSeconds(3),
        PublishingStatePollInterval = TimeSpan.FromSeconds(1),
        PublishingStateTimeout = TimeSpan.FromSeconds(3),
    };

    private static Win32LobAppContentUploadOrchestrator CreateOrchestrator(
        FakeMobileAppContentClient client, FakeAzureStorageBlockBlobUploader uploader, ManualTimeProvider timeProvider)
        => new(new IntuneWinContentExtractor(), client, uploader, timeProvider, (delay, _) =>
        {
            timeProvider.Advance(delay);
            return Task.CompletedTask;
        });

    [TestMethod]
    public async Task PublishContentAsync_MatchingInputHash_SkipsUploadAndOnlyPatchesNotes()
    {
        var client = new FakeMobileAppContentClient();
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var metadata = CreateMetadata();

        var result = await orchestrator.PublishContentAsync(
            "app-1", CreatePackage(inputHash: "same-hash"), storedInputHash: "same-hash", metadata, FastOptions(), CancellationToken.None);

        Assert.AreEqual(ContentUploadOutcome.SkippedUnchanged, result.Outcome);
        Assert.IsNull(result.ContentVersionId);
        Assert.HasCount(1, client.PatchedNotes);
        Assert.AreEqual(metadata.Serialize(), client.PatchedNotes[0]);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_HappyPath_RunsAllStepsAndReturnsUploaded()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("processing");
        client.PublishingStates.Enqueue("published");
        var uploader = new FakeAzureStorageBlockBlobUploader();
        var orchestrator = CreateOrchestrator(client, uploader, new ManualTimeProvider());
        var metadata = CreateMetadata();

        var result = await orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: "old-hash", metadata, FastOptions(), CancellationToken.None);

        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.Outcome);
        Assert.AreEqual("cv-1", result.ContentVersionId);
        Assert.HasCount(1, client.CreateContentFileCalls);
        Assert.AreEqual("IntunePackage.intunewin", client.CreateContentFileCalls[0].Name);
        Assert.AreEqual(4, client.CreateContentFileCalls[0].Size);
        Assert.HasCount(1, client.CommitFileCalls);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, client.CommitFileCalls[0].EncryptionKey);
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
        Assert.HasCount(1, client.PatchedNotes);
        Assert.AreEqual(metadata.Serialize(), client.PatchedNotes[0]);
    }

    [TestMethod]
    public async Task PublishContentAsync_AzureStorageUriRequestFailed_ThrowsContentUploadFailedException()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestFailed"));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadFailedException>(() => orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None));

        Assert.AreEqual("azureStorageUriRequest", ex.Stage);
        Assert.AreEqual("azureStorageUriRequestFailed", ex.UploadState);
    }

    [TestMethod]
    public async Task PublishContentAsync_SuccessWithoutExpiration_ThrowsGraphRequestExceptionInsteadOfDefaultingToNow()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob", includeExpiration: false));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        await Assert.ThrowsExactlyAsync<GraphRequestException>(() => orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None));
    }

    [TestMethod]
    public async Task PublishContentAsync_AzureStorageUriRequestNeverSucceeds_ThrowsContentUploadTimedOutException()
    {
        var client = new FakeMobileAppContentClient();
        for (var i = 0; i < 10; i++)
        {
            client.FileResponses.Enqueue(FileState("azureStorageUriRequestPending"));
        }

        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadTimedOutException>(() => orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None));

        Assert.AreEqual("azureStorageUriRequest", ex.Stage);
    }

    [TestMethod]
    public async Task PublishContentAsync_CommitFails_ThrowsContentUploadFailedExceptionAndDoesNotPatchApp()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileFailed"));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadFailedException>(() => orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None));

        Assert.AreEqual("commit", ex.Stage);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_PublishingStateNeverPublished_ThrowsContentUploadTimedOutException()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        for (var i = 0; i < 10; i++)
        {
            client.PublishingStates.Enqueue("processing");
        }

        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadTimedOutException>(() => orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None));

        Assert.AreEqual("publishingState", ex.Stage);
        // committedContentVersion is patched before waiting for publishingState - the point of no return already happened.
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_UploaderRequestsRenewal_CallsRenewUploadAndPollsRenewalState()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("azureStorageUriRenewalSuccess", "https://sas.example/blob-renewed"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
        var uploader = new FakeAzureStorageBlockBlobUploader { InvokeRenewal = true };
        var orchestrator = CreateOrchestrator(client, uploader, new ManualTimeProvider());

        await orchestrator.PublishContentAsync(
            "app-1", CreatePackage(), storedInputHash: null, CreateMetadata(), FastOptions(), CancellationToken.None);

        Assert.AreEqual(1, client.RenewUploadCallCount);
        Assert.IsNotNull(uploader.LastRenewal);
        Assert.AreEqual("https://sas.example/blob-renewed", uploader.LastRenewal!.Uri.ToString());
    }
}
