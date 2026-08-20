using System.IO.Compression;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class MobileAppContentUploadOrchestratorTests
{
    private const string WindowsODataType = "#microsoft.graph.win32LobApp";

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

        public Queue<Exception> PatchNotesFailures { get; } = new();

        public List<(string Name, long Size, long SizeEncrypted)> CreateContentFileCalls { get; } = [];

        public List<FileEncryptionInfoPayload> CommitFileCalls { get; } = [];

        public int RenewUploadCallCount { get; private set; }

        public string? PatchedCommittedContentVersion { get; private set; }

        public List<string> PatchedNotes { get; } = [];

        public List<bool> UseBetaCalls { get; } = [];

        public List<string> ODataTypeCalls { get; } = [];

        public Task<string> CreateContentVersionAsync(string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult("cv-1");
        }

        public Task<string> CreateContentFileAsync(
            string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            CreateContentFileCalls.Add((name, size, sizeEncrypted));
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult("file-1");
        }

        public Task<MobileAppContentFileResponse> GetContentFileAsync(
            string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            ODataTypeCalls.Add(oDataType);
            if (FileResponses.Count == 0)
            {
                throw new InvalidOperationException("No more queued file responses.");
            }

            return Task.FromResult(FileResponses.Dequeue());
        }

        public Task RenewUploadAsync(string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            RenewUploadCallCount++;
            ODataTypeCalls.Add(oDataType);
            return Task.CompletedTask;
        }

        public Task CommitFileAsync(
            string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            CommitFileCalls.Add(fileEncryptionInfo);
            ODataTypeCalls.Add(oDataType);
            return Task.CompletedTask;
        }

        public Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            PatchedCommittedContentVersion = contentVersionId;
            ODataTypeCalls.Add(oDataType);
            UseBetaCalls.Add(useBeta);
            return Task.CompletedTask;
        }

        public Task PatchNotesAsync(string appId, string notes, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            if (PatchNotesFailures.TryDequeue(out var failure))
            {
                throw failure;
            }

            PatchedNotes.Add(notes);
            ODataTypeCalls.Add(oDataType);
            UseBetaCalls.Add(useBeta);
            return Task.CompletedTask;
        }

        public Task<string> GetPublishingStateAsync(string appId, bool useBeta, CancellationToken cancellationToken)
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

    private PublishableContent CreateContent(string inputHash = "input-hash-new")
        => new(CreateIntuneWinFile(), inputHash);

    private static ContentUploadOptions FastOptions() => new()
    {
        AzureStorageUriPollInterval = TimeSpan.FromSeconds(1),
        AzureStorageUriTimeout = TimeSpan.FromSeconds(3),
        CommitPollInterval = TimeSpan.FromSeconds(1),
        CommitTimeout = TimeSpan.FromSeconds(3),
        PublishingStatePollInterval = TimeSpan.FromSeconds(1),
        PublishingStateTimeout = TimeSpan.FromSeconds(3),
    };

    private static MobileAppContentUploadOrchestrator CreateOrchestrator(
        FakeMobileAppContentClient client, FakeAzureStorageBlockBlobUploader uploader, ManualTimeProvider timeProvider)
        => new(client, uploader, timeProvider, (delay, _) =>
        {
            timeProvider.Advance(delay);
            return Task.CompletedTask;
        });

    private static Task<ContentUploadResult> PublishAsync(
        MobileAppContentUploadOrchestrator orchestrator, string appId, PublishableContent content, string? storedInputHash,
        ManagementMetadata metadata, ContentUploadOptions options, bool useBeta = false)
        => orchestrator.PublishContentAsync(
            appId, content, storedInputHash, metadata, options, new IntuneWinContentExtractor(), WindowsODataType, useBeta, CancellationToken.None);

    [TestMethod]
    public async Task PublishContentAsync_MatchingInputHash_SkipsUploadAndOnlyPatchesNotes()
    {
        var client = new FakeMobileAppContentClient();
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var metadata = CreateMetadata();

        var result = await PublishAsync(
            orchestrator, "app-1", CreateContent(inputHash: "same-hash"), storedInputHash: "same-hash", metadata, FastOptions());

        Assert.AreEqual(ContentUploadOutcome.SkippedUnchanged, result.Outcome);
        Assert.IsNull(result.ContentVersionId);
        Assert.HasCount(1, client.PatchedNotes);
        Assert.AreEqual(metadata.Serialize(), client.PatchedNotes[0]);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_MatchingInputHash_PatchNotesReturnsPublishingStateNotPublished_WaitsAndRetries()
    {
        var client = new FakeMobileAppContentClient();
        client.PatchNotesFailures.Enqueue(new GraphRequestException(
            "Invalid operation: app's PublishingState is not 'Published'.", 400, null, null));
        client.PublishingStates.Enqueue("processing");
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var metadata = CreateMetadata();

        var result = await PublishAsync(
            orchestrator, "app-1", CreateContent(inputHash: "same-hash"), storedInputHash: "same-hash", metadata, FastOptions());

        Assert.AreEqual(ContentUploadOutcome.SkippedUnchanged, result.Outcome);
        Assert.HasCount(1, client.PatchedNotes);
        Assert.AreEqual(metadata.Serialize(), client.PatchedNotes[0]);
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

        var result = await PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: "old-hash", metadata, FastOptions());

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
        Assert.IsTrue(client.UseBetaCalls.TrueForAll(b => !b), "useBeta should stay false end to end for a Windows publish.");
        Assert.IsTrue(client.ODataTypeCalls.TrueForAll(t => t == WindowsODataType));
    }

    [TestMethod]
    public async Task PublishContentAsync_UseBetaTrue_PassedThroughToEveryContentCall()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        await orchestrator.PublishContentAsync(
            "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions(),
            new IntuneWinContentExtractor(), "#microsoft.graph.macOSPkgApp", useBeta: true, CancellationToken.None);

        Assert.IsTrue(client.UseBetaCalls.Count > 0);
        Assert.IsTrue(client.UseBetaCalls.TrueForAll(b => b));
        Assert.IsTrue(client.ODataTypeCalls.TrueForAll(t => t == "#microsoft.graph.macOSPkgApp"));
    }

    [TestMethod]
    public async Task PublishContentAsync_AzureStorageUriRequestFailed_ThrowsContentUploadFailedException()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestFailed"));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadFailedException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

        Assert.AreEqual("azureStorageUriRequest", ex.Stage);
        Assert.AreEqual("azureStorageUriRequestFailed", ex.UploadState);
    }

    [TestMethod]
    public async Task PublishContentAsync_SuccessWithoutExpiration_ThrowsGraphRequestExceptionInsteadOfDefaultingToNow()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob", includeExpiration: false));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));
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

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadTimedOutException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

        Assert.AreEqual("azureStorageUriRequest", ex.Stage);
    }

    [TestMethod]
    public async Task PublishContentAsync_CommitFails_ThrowsContentUploadFailedExceptionAndDoesNotPatchApp()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileFailed"));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadFailedException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

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

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadTimedOutException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

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

        await PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions());

        Assert.AreEqual(1, client.RenewUploadCallCount);
        Assert.IsNotNull(uploader.LastRenewal);
        Assert.AreEqual("https://sas.example/blob-renewed", uploader.LastRenewal!.Uri.ToString());
    }
}
