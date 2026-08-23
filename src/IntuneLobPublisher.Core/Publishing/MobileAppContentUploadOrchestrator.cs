using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

public enum ContentUploadOutcome
{
    /// <summary>Content was uploaded or an interrupted committed file was activated successfully.</summary>
    Uploaded,

    /// <summary>The stored inputHash matched; only the notes metadata was refreshed.</summary>
    SkippedUnchanged,
}

public sealed record ContentUploadResult(ContentUploadOutcome Outcome, string? ContentVersionId);

/// <summary>The staged content publish uploads: the path to the file <c>extractor</c> reads, and the inputHash used for the skip decision.</summary>
/// <param name="ContentPath">The <c>.intunewin</c> path (Windows) or staged <c>.pkg</c> path (macOS).</param>
/// <param name="InputHash">From <c>package-metadata.json</c> (<see cref="Packaging.PackageMetadata.InputHash"/>).</param>
public sealed record PublishableContent(string ContentPath, string InputHash);

/// <summary>
/// Runs the mobile LOB app content upload flow (doc/issues/issue-003-intune-graph-win32.md "Content upload
/// flow", steps 1-10): extract/encrypt the content, create a content version and file, upload the
/// encrypted payload, commit it, activate it, wait for publishing, and refresh the app's notes metadata.
/// Before the hash-based skip decision it reads the app's <c>publishingState</c>; an app still in
/// <c>processing</c> is waited on. For <c>notPublished</c>, an interrupted first content version is
/// recovered without creating a second version: uncommitted files are replaced, while a sole committed
/// file for the same input resumes at app activation. Ambiguous states fail without destructive cleanup.
/// Platform-neutral: the caller supplies the right <see cref="IUploadableContentExtractor"/>
/// (<see cref="IntuneWinContentExtractor"/> for Windows, <see cref="PkgContentPreparer"/> for macOS) and
/// whether this app's Graph calls must stay on <c>/beta/</c> (macOS <c>AppType: pkg</c>).
/// </summary>
public interface IMobileAppContentUploadOrchestrator
{
    /// <summary>
    /// Publishes <paramref name="content"/> to the app identified by <paramref name="appId"/>.
    /// Skips the upload (steps 1-9) when the app is already <c>published</c> and
    /// <paramref name="storedInputHash"/> matches <c>content.InputHash</c>, but always refreshes
    /// <c>notes</c> from <paramref name="metadata"/>. An app in <c>notPublished</c> state resolves and
    /// safely recovers its first content version before deciding whether upload or activation is needed.
    /// </summary>
    Task<ContentUploadResult> PublishContentAsync(
        string appId,
        PublishableContent content,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        IUploadableContentExtractor extractor,
        string oDataType,
        bool useBeta,
        CancellationToken cancellationToken);
}

public sealed class MobileAppContentUploadOrchestrator : IMobileAppContentUploadOrchestrator
{
    private static readonly HashSet<string> AzureStorageUriRequestFailureStates = ["azureStorageUriRequestFailed", "azureStorageUriRequestTimedOut"];
    private static readonly HashSet<string> AzureStorageUriRenewalFailureStates = ["azureStorageUriRenewalFailed", "azureStorageUriRenewalTimedOut"];
    private static readonly HashSet<string> CommitFailureStates = ["commitFileFailed", "commitFileTimedOut"];

    private readonly IMobileAppContentClient _contentClient;
    private readonly IAzureStorageBlockBlobUploader _blobUploader;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public MobileAppContentUploadOrchestrator(
        IMobileAppContentClient contentClient,
        IAzureStorageBlockBlobUploader blobUploader,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _contentClient = contentClient;
        _blobUploader = blobUploader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<ContentUploadResult> PublishContentAsync(
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
        var publishingState = await _contentClient
            .GetPublishingStateAsync(appId, useBeta, cancellationToken)
            .ConfigureAwait(false);

        switch (publishingState)
        {
            case "published":
                break;
            case "processing":
                await PollPublishingStateAsync(
                        appId, options.PublishingStatePollInterval, options.PublishingStateTimeout, useBeta, cancellationToken)
                    .ConfigureAwait(false);
                publishingState = "published";
                break;
            case "notPublished":
                // A matching inputHash is not sufficient here: the app has no active content version.
                break;
            default:
                throw UnknownPublishingState(appId, publishingState);
        }

        if (publishingState == "published"
            && PublishGuard.EvaluateContentUpload(storedInputHash, content.InputHash) == ContentUploadDecision.Skip)
        {
            await _contentClient.PatchNotesAsync(appId, metadata.Serialize(), oDataType, useBeta, cancellationToken).ConfigureAwait(false);
            return new ContentUploadResult(ContentUploadOutcome.SkippedUnchanged, null);
        }

        var recovery = publishingState == "notPublished"
            ? await ResolveNotPublishedContentAsync(
                    appId, storedInputHash, content.InputHash, oDataType, useBeta, cancellationToken)
                .ConfigureAwait(false)
            : ContentRecoveryPlan.CreateNew;

        if (recovery.ResumeActivation)
        {
            return await ActivateContentVersionAsync(
                    appId, recovery.ContentVersionId!, metadata, options, oDataType, useBeta, cancellationToken)
                .ConfigureAwait(false);
        }

        // Prepare the local payload before deleting stale Graph files. A local extraction/encryption
        // failure must leave the remote recovery state untouched so the next run can try again.
        using var uploadable = extractor.Extract(content.ContentPath);

        var contentVersionId = recovery.ContentVersionId
            ?? await _contentClient.CreateContentVersionAsync(appId, oDataType, useBeta, cancellationToken).ConfigureAwait(false);

        foreach (var staleFileId in recovery.UncommittedFileIds)
        {
            await _contentClient
                .DeleteContentFileAsync(appId, contentVersionId, staleFileId, oDataType, useBeta, cancellationToken)
                .ConfigureAwait(false);
        }

        var fileId = await _contentClient.CreateContentFileAsync(
                appId, contentVersionId, uploadable.ContentFileName, uploadable.UnencryptedContentSize, uploadable.EncryptedContentSize,
                oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        var readyFile = await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "azureStorageUriRequest",
                successState: "azureStorageUriRequestSuccess", failureStates: AzureStorageUriRequestFailureStates,
                options.AzureStorageUriPollInterval, options.AzureStorageUriTimeout, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        var sasUri = new Uri(RequireAzureStorageUri(readyFile, "azureStorageUriRequestSuccess"));
        var expiresAt = RequireAzureStorageUriExpiration(readyFile, "azureStorageUriRequestSuccess");

        using (var payloadStream = uploadable.OpenEncryptedContentStream())
        {
            await _blobUploader.UploadAsync(
                sasUri,
                expiresAt,
                payloadStream,
                ct => RenewSasUriAsync(appId, contentVersionId, fileId, options, oDataType, useBeta, ct),
                options,
                cancellationToken).ConfigureAwait(false);
        }

        var encryptionInfo = uploadable.EncryptionInfo;
        await _contentClient.CommitFileAsync(
            appId, contentVersionId, fileId,
            new FileEncryptionInfoPayload
            {
                EncryptionKey = encryptionInfo.EncryptionKey,
                InitializationVector = encryptionInfo.InitializationVector,
                Mac = encryptionInfo.Mac,
                MacKey = encryptionInfo.MacKey,
                ProfileIdentifier = encryptionInfo.ProfileIdentifier,
                FileDigest = encryptionInfo.FileDigest,
                FileDigestAlgorithm = encryptionInfo.FileDigestAlgorithm,
            },
            oDataType,
            useBeta,
            cancellationToken).ConfigureAwait(false);

        await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "commit",
                successState: "commitFileSuccess", failureStates: CommitFailureStates,
                options.CommitPollInterval, options.CommitTimeout, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        return await ActivateContentVersionAsync(
                appId, contentVersionId, metadata, options, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ContentRecoveryPlan> ResolveNotPublishedContentAsync(
        string appId,
        string? storedInputHash,
        string currentInputHash,
        string oDataType,
        bool useBeta,
        CancellationToken cancellationToken)
    {
        var contentVersions = await _contentClient
            .ListContentVersionsAsync(appId, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        if (contentVersions.Count == 0)
        {
            return ContentRecoveryPlan.CreateNew;
        }

        if (contentVersions.Count != 1)
        {
            throw UnsafeRecoveryState(
                appId,
                $"Graph returned {contentVersions.Count} content versions for a notPublished app; expected at most one.");
        }

        var contentVersionId = contentVersions[0].Id!;
        var files = await _contentClient
            .ListContentFilesAsync(appId, contentVersionId, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        if (files.Any(file => file.IsCommitted is null))
        {
            throw UnsafeRecoveryState(
                appId,
                $"Graph omitted isCommitted for a file in content version '{contentVersionId}'.");
        }

        var committedFiles = files.Where(file => file.IsCommitted is true).ToList();
        var uncommittedFiles = files.Where(file => file.IsCommitted is false).ToList();

        if (committedFiles.Count == 0)
        {
            return new ContentRecoveryPlan(
                contentVersionId,
                uncommittedFiles.Select(file => file.Id!).ToArray(),
                ResumeActivation: false);
        }

        if (uncommittedFiles.Count != 0 || committedFiles.Count != 1)
        {
            throw UnsafeRecoveryState(
                appId,
                $"Content version '{contentVersionId}' has an ambiguous mix or count of committed and uncommitted files.");
        }

        if (!string.Equals(storedInputHash, currentInputHash, StringComparison.Ordinal))
        {
            throw UnsafeRecoveryState(
                appId,
                $"Content version '{contentVersionId}' contains a committed file that cannot be tied to the current inputHash.");
        }

        return new ContentRecoveryPlan(contentVersionId, [], ResumeActivation: true);
    }

    private async Task<ContentUploadResult> ActivateContentVersionAsync(
        string appId,
        string contentVersionId,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        string oDataType,
        bool useBeta,
        CancellationToken cancellationToken)
    {
        // Point of no return (doc/00-overview.md 6.10): existing clients are served this content from here on.
        await _contentClient
            .PatchCommittedContentVersionAsync(appId, contentVersionId, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        await PollPublishingStateAsync(
                appId, options.PublishingStatePollInterval, options.PublishingStateTimeout, useBeta, cancellationToken)
            .ConfigureAwait(false);

        await _contentClient
            .PatchNotesAsync(appId, metadata.Serialize(), oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        return new ContentUploadResult(ContentUploadOutcome.Uploaded, contentVersionId);
    }

    private async Task<SasUriRenewal> RenewSasUriAsync(
        string appId, string contentVersionId, string fileId, ContentUploadOptions options, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        await _contentClient.RenewUploadAsync(appId, contentVersionId, fileId, oDataType, useBeta, cancellationToken).ConfigureAwait(false);
        var renewed = await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "azureStorageUriRenewal",
                successState: "azureStorageUriRenewalSuccess", failureStates: AzureStorageUriRenewalFailureStates,
                options.AzureStorageUriPollInterval, options.AzureStorageUriTimeout, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        return new SasUriRenewal(
            new Uri(RequireAzureStorageUri(renewed, "azureStorageUriRenewalSuccess")),
            RequireAzureStorageUriExpiration(renewed, "azureStorageUriRenewalSuccess"));
    }

    private static string RequireAzureStorageUri(MobileAppContentFileResponse file, string uploadState)
        => file.AzureStorageUri
            ?? throw new GraphRequestException($"Graph reported '{uploadState}' without an azureStorageUri.", null, null, null);

    // A missing expiry is treated as a Graph contract violation rather than defaulted to "now": defaulting
    // would silently force an immediate renewUpload call on every upload instead of surfacing the bug.
    private static DateTimeOffset RequireAzureStorageUriExpiration(MobileAppContentFileResponse file, string uploadState)
        => file.AzureStorageUriExpirationDateTime
            ?? throw new GraphRequestException($"Graph reported '{uploadState}' without an azureStorageUriExpirationDateTime.", null, null, null);

    private static GraphRequestException UnknownPublishingState(string appId, string? publishingState)
        => new(
            $"Graph returned unsupported publishingState '{publishingState ?? "(null)"}' for app '{appId}'. " +
            "Expected 'notPublished', 'processing' or 'published'.",
            null,
            null,
            null);

    private static GraphRequestException UnsafeRecoveryState(string appId, string detail)
        => new(
            $"Cannot safely recover content for notPublished app '{appId}'. {detail} " +
            "No app, content version, or committed file was deleted.",
            null,
            null,
            null);

    private async Task<MobileAppContentFileResponse> PollFileStateAsync(
        string appId, string contentVersionId, string fileId, string stage,
        string successState, IReadOnlySet<string> failureStates,
        TimeSpan pollInterval, TimeSpan timeout, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var file = await _contentClient.GetContentFileAsync(appId, contentVersionId, fileId, oDataType, useBeta, cancellationToken).ConfigureAwait(false);
            if (string.Equals(file.UploadState, successState, StringComparison.Ordinal))
            {
                return file;
            }

            if (failureStates.Contains(file.UploadState))
            {
                throw new ContentUploadFailedException(stage, file.UploadState);
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new ContentUploadTimedOutException(stage, timeout);
            }

            await _delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollPublishingStateAsync(string appId, TimeSpan pollInterval, TimeSpan timeout, bool useBeta, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var state = await _contentClient.GetPublishingStateAsync(appId, useBeta, cancellationToken).ConfigureAwait(false);
            if (string.Equals(state, "published", StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(state, "processing", StringComparison.Ordinal)
                && !string.Equals(state, "notPublished", StringComparison.Ordinal))
            {
                throw UnknownPublishingState(appId, state);
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new ContentUploadTimedOutException("publishingState", timeout);
            }

            await _delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ContentRecoveryPlan(
        string? ContentVersionId,
        IReadOnlyList<string> UncommittedFileIds,
        bool ResumeActivation)
    {
        public static ContentRecoveryPlan CreateNew { get; } = new(null, [], ResumeActivation: false);
    }
}
