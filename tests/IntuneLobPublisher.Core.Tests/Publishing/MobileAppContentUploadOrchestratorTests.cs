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

        public string? CommittedContentVersion { get; set; }

        public List<MobileAppContentResponse> ContentVersions { get; } = [];

        public Dictionary<string, List<MobileAppContentFileResponse>> ContentFiles { get; } = [];

        public List<(string Name, long Size, long SizeEncrypted)> CreateContentFileCalls { get; } = [];

        public List<string> CreateContentFileContentVersionIds { get; } = [];

        public List<FileEncryptionInfoPayload> CommitFileCalls { get; } = [];

        public int CreateContentVersionCallCount { get; private set; }

        public int RenewUploadCallCount { get; private set; }

        public string? PatchedCommittedContentVersion { get; private set; }

        public List<string> PatchedNotes { get; } = [];

        public List<bool> UseBetaCalls { get; } = [];

        public List<string> ODataTypeCalls { get; } = [];

        public Task<string> CreateContentVersionAsync(string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            CreateContentVersionCallCount++;
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult("cv-1");
        }

        public Task<IReadOnlyList<MobileAppContentResponse>> ListContentVersionsAsync(
            string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult<IReadOnlyList<MobileAppContentResponse>>(ContentVersions.ToArray());
        }

        public Task<IReadOnlyList<MobileAppContentFileResponse>> ListContentFilesAsync(
            string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult<IReadOnlyList<MobileAppContentFileResponse>>(
                ContentFiles.TryGetValue(contentVersionId, out var files) ? files.ToArray() : []);
        }

        public Task<string> CreateContentFileAsync(
            string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            CreateContentFileCalls.Add((name, size, sizeEncrypted));
            CreateContentFileContentVersionIds.Add(contentVersionId);
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.FromResult("file-1");
        }

        public Task<MobileAppContentFileResponse> GetContentFileAsync(
            string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
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
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            return Task.CompletedTask;
        }

        public Task CommitFileAsync(
            string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            CommitFileCalls.Add(fileEncryptionInfo);
            UseBetaCalls.Add(useBeta);
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
            PatchedNotes.Add(notes);
            ODataTypeCalls.Add(oDataType);
            UseBetaCalls.Add(useBeta);
            return Task.CompletedTask;
        }

        public Task<string> GetPublishingStateAsync(string appId, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            if (PublishingStates.Count == 0)
            {
                throw new InvalidOperationException("No more queued publishing states.");
            }

            return Task.FromResult(PublishingStates.Dequeue());
        }

        public Task<MobileAppContentState> GetContentStateAsync(
            string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
        {
            UseBetaCalls.Add(useBeta);
            ODataTypeCalls.Add(oDataType);
            if (PublishingStates.Count == 0)
            {
                throw new InvalidOperationException("No more queued publishing states.");
            }

            return Task.FromResult(new MobileAppContentState(PublishingStates.Dequeue(), CommittedContentVersion));
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

    private static MobileAppContentFileResponse FileState(
        string uploadState,
        string? azureStorageUri = null,
        bool includeExpiration = true,
        bool isCommitted = false,
        string id = "file-1",
        string? name = null,
        long? size = null,
        long? sizeEncrypted = null)
        => new()
        {
            Id = id,
            Name = name,
            Size = size,
            SizeEncrypted = sizeEncrypted,
            UploadState = uploadState,
            IsCommitted = isCommitted,
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
        client.PublishingStates.Enqueue("published");
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
    public async Task PublishContentAsync_ProcessingWaitsUntilPublishedBeforeEvaluatingHash()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("processing");
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var result = await PublishAsync(
            orchestrator, "app-1", CreateContent(inputHash: "same-hash"), storedInputHash: "same-hash", CreateMetadata(), FastOptions());

        Assert.AreEqual(ContentUploadOutcome.SkippedUnchanged, result.Outcome);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.HasCount(1, client.PatchedNotes);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublishedForcesUploadEvenWhenInputHashMatches()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var result = await PublishAsync(
            orchestrator, "app-1", CreateContent(inputHash: "same-hash"), storedInputHash: "same-hash", CreateMetadata(), FastOptions());

        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.Outcome);
        Assert.AreEqual(1, client.CreateContentVersionCallCount);
        Assert.HasCount(1, client.CreateContentFileCalls);
        Assert.AreEqual("cv-1", client.CreateContentFileContentVersionIds[0]);
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_IncompatibleUncommittedFile_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileFailed", isCommitted: false, id: "failed-file"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", CreateContent(inputHash: "same-hash"), storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "must be explicitly recreated");
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_CompatibleUncommittedFile_RenewsAndReusesFile()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        var content = CreateContent();
        using var extracted = new IntuneWinContentExtractor().Extract(content.ContentPath);
        client.ContentFiles["cv-1"] =
        [
            FileState(
                "commitFileFailed",
                isCommitted: false,
                id: "reusable-file",
                name: extracted.ContentFileName,
                size: extracted.UnencryptedContentSize,
                sizeEncrypted: extracted.EncryptedContentSize),
        ];
        client.FileResponses.Enqueue(FileState("azureStorageUriRenewalSuccess", "https://sas.example/renewed"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var result = await PublishAsync(
            orchestrator, "app-1", content, storedInputHash: null, CreateMetadata(), FastOptions());

        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.Outcome);
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(1, client.RenewUploadCallCount);
        Assert.HasCount(1, client.CommitFileCalls);
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_MultipleCompatibleUncommittedFiles_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        var content = CreateContent();
        using var extracted = new IntuneWinContentExtractor().Extract(content.ContentPath);
        client.ContentFiles["cv-1"] =
        [
            FileState(
                "commitFileFailed",
                isCommitted: false,
                id: "matching-file-1",
                name: extracted.ContentFileName,
                size: extracted.UnencryptedContentSize,
                sizeEncrypted: extracted.EncryptedContentSize),
            FileState(
                "commitFileFailed",
                isCommitted: false,
                id: "matching-file-2",
                name: extracted.ContentFileName,
                size: extracted.UnencryptedContentSize,
                sizeEncrypted: extracted.EncryptedContentSize),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", content, storedInputHash: null, CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "contains 2 uncommitted files");
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_CompatibleAndIncompatibleUncommittedFiles_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        var content = CreateContent();
        using var extracted = new IntuneWinContentExtractor().Extract(content.ContentPath);
        client.ContentFiles["cv-1"] =
        [
            FileState(
                "commitFileFailed",
                isCommitted: false,
                id: "compatible-file",
                name: extracted.ContentFileName,
                size: extracted.UnencryptedContentSize,
                sizeEncrypted: extracted.EncryptedContentSize),
            FileState(
                "commitFileFailed",
                isCommitted: false,
                id: "incompatible-file",
                name: "different.intunewin",
                size: 1,
                sizeEncrypted: 2),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", content, storedInputHash: null, CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "contains 2 uncommitted files");
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_UncommittedVersionReferencedAsCommitted_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient { CommittedContentVersion = "cv-1" };
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileFailed", isCommitted: false, id: "failed-file"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "referenced by committedContentVersion");
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_ExistingVersionWithoutFiles_ReusesContentVersion()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] = [];
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var result = await PublishAsync(
            orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions());

        Assert.AreEqual(ContentUploadOutcome.Uploaded, result.Outcome);
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.AreEqual("cv-1", client.CreateContentFileContentVersionIds[0]);
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_CommittedFileWithMatchingHash_ResumesActivationWithoutUpload()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileSuccess", isCommitted: true, id: "committed-file"),
        ];
        client.PublishingStates.Enqueue("published");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        await PublishAsync(orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions());

        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
        Assert.HasCount(1, client.PatchedNotes);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_CommittedFileWithMismatchedHash_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileSuccess", isCommitted: true, id: "committed-file"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "new-hash");

        await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", missingContent, storedInputHash: "old-hash", CreateMetadata(), FastOptions()));

        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
        Assert.IsEmpty(client.PatchedNotes);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_CommittedFileWithFailedSibling_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileSuccess", isCommitted: true, id: "committed-file"),
            FileState("commitFileFailed", isCommitted: false, id: "failed-file"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "ambiguous mix or count");
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_MultipleCommittedFiles_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState("commitFileSuccess", isCommitted: true, id: "committed-file-1"),
            FileState("commitFileSuccess", isCommitted: true, id: "committed-file-2"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "ambiguous mix or count");
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    [DataRow("commitFilePending")]
    [DataRow("commitFileTimedOut")]
    [DataRow("azureStorageUriRequestTimedOut")]
    [DataRow("azureStorageUriRenewalTimedOut")]
    [DataRow("transientError")]
    public async Task PublishContentAsync_NotPublished_PendingOrUnsupportedUncommittedFile_FailsWithoutMutation(string uploadState)
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            FileState(uploadState, isCommitted: false, id: "pending-file"),
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() => PublishAsync(
            orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "uploadState is still pending or unsupported");
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.AreEqual(0, client.RenewUploadCallCount);
        Assert.IsEmpty(client.CommitFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_FileWithoutIsCommitted_FailsWithoutMutation()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentFiles["cv-1"] =
        [
            new MobileAppContentFileResponse
            {
                Id = "unknown-file",
                UploadState = "commitFileFailed",
                IsCommitted = null,
            },
        ];
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "same-hash");

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", missingContent, storedInputHash: "same-hash", CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "omitted isCommitted");
        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.IsNull(client.PatchedCommittedContentVersion);
    }

    [TestMethod]
    public async Task PublishContentAsync_NotPublished_MultipleContentVersions_FailsBeforeUpload()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("notPublished");
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-1" });
        client.ContentVersions.Add(new MobileAppContentResponse { Id = "cv-2" });
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());
        var missingContent = new PublishableContent(Path.Combine(_workspace.FullName, "not-created.intunewin"), "new-hash");

        await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", missingContent, storedInputHash: null, CreateMetadata(), FastOptions()));

        Assert.AreEqual(0, client.CreateContentVersionCallCount);
        Assert.IsEmpty(client.CreateContentFileCalls);
    }

    [TestMethod]
    public async Task PublishContentAsync_UnknownPublishingState_FailsImmediately()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("failed");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "unsupported publishingState 'failed'");
        Assert.IsEmpty(client.CreateContentFileCalls);
        Assert.IsEmpty(client.PatchedNotes);
    }

    [TestMethod]
    public async Task PublishContentAsync_HappyPath_RunsAllStepsAndReturnsUploaded()
    {
        var client = new FakeMobileAppContentClient();
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("published");
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
        client.PublishingStates.Enqueue("published");
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
        client.PublishingStates.Enqueue("published");
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob", includeExpiration: false));
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));
    }

    [TestMethod]
    public async Task PublishContentAsync_AzureStorageUriRequestNeverSucceeds_ThrowsContentUploadTimedOutException()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("published");
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
        client.PublishingStates.Enqueue("published");
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
        client.PublishingStates.Enqueue("published");
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
    public async Task PublishContentAsync_UnknownPostCommitPublishingState_FailsImmediately()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("published");
        client.FileResponses.Enqueue(FileState("azureStorageUriRequestSuccess", "https://sas.example/blob"));
        client.FileResponses.Enqueue(FileState("commitFileSuccess"));
        client.PublishingStates.Enqueue("unexpected");
        var orchestrator = CreateOrchestrator(client, new FakeAzureStorageBlockBlobUploader(), new ManualTimeProvider());

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(() =>
            PublishAsync(orchestrator, "app-1", CreateContent(), storedInputHash: null, CreateMetadata(), FastOptions()));

        StringAssert.Contains(ex.Message, "unsupported publishingState 'unexpected'");
        Assert.AreEqual("cv-1", client.PatchedCommittedContentVersion);
        Assert.IsEmpty(client.PatchedNotes);
    }

    [TestMethod]
    public async Task PublishContentAsync_UploaderRequestsRenewal_CallsRenewUploadAndPollsRenewalState()
    {
        var client = new FakeMobileAppContentClient();
        client.PublishingStates.Enqueue("published");
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
