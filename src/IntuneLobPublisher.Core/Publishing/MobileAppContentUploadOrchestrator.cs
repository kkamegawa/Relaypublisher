using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

public enum ContentUploadOutcome
{
    /// <summary>A new content version was created, uploaded, committed and activated.</summary>
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
/// Platform-neutral: the caller supplies the right <see cref="IUploadableContentExtractor"/>
/// (<see cref="IntuneWinContentExtractor"/> for Windows, <see cref="PkgContentPreparer"/> for macOS) and
/// whether this app's Graph calls must stay on <c>/beta/</c> (macOS <c>AppType: pkg</c>).
/// </summary>
public interface IMobileAppContentUploadOrchestrator
{
    /// <summary>
    /// Publishes <paramref name="content"/> to the app identified by <paramref name="appId"/>.
    /// Skips the upload (steps 1-9) when <paramref name="storedInputHash"/> already matches
    /// <c>content.InputHash</c>, but always refreshes <c>notes</c> from <paramref name="metadata"/>.
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

    /// <summary>
    /// Waits until the app publishingState leaves "processing". Graph rejects a full app-payload PATCH
    /// while a previous content commit is still finishing, so callers that issue such a PATCH ahead of
    /// (or independently of) a content upload - currently <see cref="IPlatformAppPublisher.UpdateAppAsync"/> -
    /// should call this first, in case the app was left mid-"processing" by an interrupted previous run.
    /// Unlike waiting for "published" specifically, this accepts either "notPublished" or "published" as
    /// ready-to-write: a brand-new app that has never had a content version committed sits at
    /// "notPublished" indefinitely until its first <c>committedContentVersion</c> PATCH, so requiring
    /// "published" here would deadlock that case.
    /// </summary>
    Task WaitWhilePublishingStateProcessingAsync(
        string appId, ContentUploadOptions options, bool useBeta, CancellationToken cancellationToken);
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
        if (PublishGuard.EvaluateContentUpload(storedInputHash, content.InputHash) == ContentUploadDecision.Skip)
        {
            // Nothing else in the skip path waits out a stale "processing" left by an interrupted
            // previous run, so guard explicitly before this PATCH (see WaitWhilePublishingStateProcessingAsync).
            await WaitWhilePublishingStateProcessingAsync(appId, options, useBeta, cancellationToken).ConfigureAwait(false);
            await _contentClient.PatchNotesAsync(appId, metadata.Serialize(), oDataType, useBeta, cancellationToken).ConfigureAwait(false);
            return new ContentUploadResult(ContentUploadOutcome.SkippedUnchanged, null);
        }

        using var uploadable = extractor.Extract(content.ContentPath);

        var contentVersionId = await _contentClient.CreateContentVersionAsync(appId, oDataType, useBeta, cancellationToken).ConfigureAwait(false);
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

        // Point of no return (doc/00-overview.md 6.10): existing clients are served this content from here on.
        // No pre-guard here: waiting for publishingState to leave "processing" before this call would
        // deadlock a brand-new app, which sits at "notPublished" until this very PATCH gives it a
        // committedContentVersion for the first time.
        await _contentClient.PatchCommittedContentVersionAsync(appId, contentVersionId, oDataType, useBeta, cancellationToken)
            .ConfigureAwait(false);

        await PollPublishingStateAsync(appId, options.PublishingStatePollInterval, options.PublishingStateTimeout, useBeta, cancellationToken)
            .ConfigureAwait(false);

        // publishingState is already confirmed "published" by the poll above, so this PATCH needs no guard.
        await _contentClient.PatchNotesAsync(appId, metadata.Serialize(), oDataType, useBeta, cancellationToken).ConfigureAwait(false);

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

    public async Task WaitWhilePublishingStateProcessingAsync(
        string appId, ContentUploadOptions options, bool useBeta, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + options.PublishingStateTimeout;
        while (true)
        {
            var state = await _contentClient.GetPublishingStateAsync(appId, useBeta, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(state, "processing", StringComparison.Ordinal))
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new ContentUploadTimedOutException("publishingState", options.PublishingStateTimeout);
            }

            await _delayAsync(options.PublishingStatePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

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

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                throw new ContentUploadTimedOutException("publishingState", timeout);
            }

            await _delayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
