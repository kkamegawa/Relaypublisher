using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Publishing;

public enum ContentUploadOutcome
{
    /// <summary>A new content version was created, uploaded, committed and activated.</summary>
    Uploaded,

    /// <summary>The stored inputHash matched; only the notes metadata was refreshed.</summary>
    SkippedUnchanged,
}

public sealed record ContentUploadResult(ContentUploadOutcome Outcome, string? ContentVersionId);

/// <summary>
/// Runs the Win32 app content upload flow (doc/issues/issue-003-intune-graph-win32.md "Content upload
/// flow", steps 1-10): extract the .intunewin, create a content version and file, upload the encrypted
/// payload, commit it, activate it, wait for publishing, and refresh the app's notes metadata.
/// </summary>
public interface IWin32LobAppContentUploadOrchestrator
{
    /// <summary>
    /// Publishes <paramref name="package"/>'s content to the app identified by <paramref name="appId"/>.
    /// Skips the upload (steps 1-9) when <paramref name="storedInputHash"/> already matches
    /// <c>package.InputHash</c>, but always refreshes <c>notes</c> from <paramref name="metadata"/>.
    /// </summary>
    Task<ContentUploadResult> PublishContentAsync(
        string appId,
        IntuneWinPackageResult package,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        CancellationToken cancellationToken);
}

public sealed class Win32LobAppContentUploadOrchestrator : IWin32LobAppContentUploadOrchestrator
{
    private static readonly HashSet<string> AzureStorageUriRequestFailureStates = ["azureStorageUriRequestFailed", "azureStorageUriRequestTimedOut"];
    private static readonly HashSet<string> AzureStorageUriRenewalFailureStates = ["azureStorageUriRenewalFailed", "azureStorageUriRenewalTimedOut"];
    private static readonly HashSet<string> CommitFailureStates = ["commitFileFailed", "commitFileTimedOut"];

    private readonly IIntuneWinContentExtractor _extractor;
    private readonly IMobileAppContentClient _contentClient;
    private readonly IAzureStorageBlockBlobUploader _blobUploader;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public Win32LobAppContentUploadOrchestrator(
        IIntuneWinContentExtractor extractor,
        IMobileAppContentClient contentClient,
        IAzureStorageBlockBlobUploader blobUploader,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _extractor = extractor;
        _contentClient = contentClient;
        _blobUploader = blobUploader;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<ContentUploadResult> PublishContentAsync(
        string appId,
        IntuneWinPackageResult package,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
    {
        if (PublishGuard.EvaluateContentUpload(storedInputHash, package.InputHash) == ContentUploadDecision.Skip)
        {
            await _contentClient.PatchNotesAsync(appId, metadata.Serialize(), cancellationToken).ConfigureAwait(false);
            return new ContentUploadResult(ContentUploadOutcome.SkippedUnchanged, null);
        }

        using var content = _extractor.Extract(package.IntuneWinPath);

        var contentVersionId = await _contentClient.CreateContentVersionAsync(appId, cancellationToken).ConfigureAwait(false);
        var fileId = await _contentClient.CreateContentFileAsync(
                appId, contentVersionId, content.ContentFileName, content.UnencryptedContentSize, content.EncryptedContentSize, cancellationToken)
            .ConfigureAwait(false);

        var readyFile = await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "azureStorageUriRequest",
                successState: "azureStorageUriRequestSuccess", failureStates: AzureStorageUriRequestFailureStates,
                options.AzureStorageUriPollInterval, options.AzureStorageUriTimeout, cancellationToken)
            .ConfigureAwait(false);

        var sasUri = new Uri(RequireAzureStorageUri(readyFile, "azureStorageUriRequestSuccess"));
        var expiresAt = readyFile.AzureStorageUriExpirationDateTime ?? _timeProvider.GetUtcNow();

        using (var payloadStream = content.OpenEncryptedContentStream())
        {
            await _blobUploader.UploadAsync(
                sasUri,
                expiresAt,
                payloadStream,
                ct => RenewSasUriAsync(appId, contentVersionId, fileId, options, ct),
                options,
                cancellationToken).ConfigureAwait(false);
        }

        var encryptionInfo = content.EncryptionInfo;
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
            cancellationToken).ConfigureAwait(false);

        await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "commit",
                successState: "commitFileSuccess", failureStates: CommitFailureStates,
                options.CommitPollInterval, options.CommitTimeout, cancellationToken)
            .ConfigureAwait(false);

        // Point of no return (doc/00-overview.md 6.10): existing clients are served this content from here on.
        await _contentClient.PatchCommittedContentVersionAsync(appId, contentVersionId, cancellationToken).ConfigureAwait(false);

        await PollPublishingStateAsync(appId, options.PublishingStatePollInterval, options.PublishingStateTimeout, cancellationToken)
            .ConfigureAwait(false);

        await _contentClient.PatchNotesAsync(appId, metadata.Serialize(), cancellationToken).ConfigureAwait(false);

        return new ContentUploadResult(ContentUploadOutcome.Uploaded, contentVersionId);
    }

    private async Task<SasUriRenewal> RenewSasUriAsync(
        string appId, string contentVersionId, string fileId, ContentUploadOptions options, CancellationToken cancellationToken)
    {
        await _contentClient.RenewUploadAsync(appId, contentVersionId, fileId, cancellationToken).ConfigureAwait(false);
        var renewed = await PollFileStateAsync(
                appId, contentVersionId, fileId, stage: "azureStorageUriRenewal",
                successState: "azureStorageUriRenewalSuccess", failureStates: AzureStorageUriRenewalFailureStates,
                options.AzureStorageUriPollInterval, options.AzureStorageUriTimeout, cancellationToken)
            .ConfigureAwait(false);

        return new SasUriRenewal(
            new Uri(RequireAzureStorageUri(renewed, "azureStorageUriRenewalSuccess")),
            renewed.AzureStorageUriExpirationDateTime ?? _timeProvider.GetUtcNow());
    }

    private static string RequireAzureStorageUri(MobileAppContentFileResponse file, string uploadState)
        => file.AzureStorageUri
            ?? throw new GraphRequestException($"Graph reported '{uploadState}' without an azureStorageUri.", null, null, null);

    private async Task<MobileAppContentFileResponse> PollFileStateAsync(
        string appId, string contentVersionId, string fileId, string stage,
        string successState, IReadOnlySet<string> failureStates,
        TimeSpan pollInterval, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var file = await _contentClient.GetContentFileAsync(appId, contentVersionId, fileId, cancellationToken).ConfigureAwait(false);
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

    private async Task PollPublishingStateAsync(string appId, TimeSpan pollInterval, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var state = await _contentClient.GetPublishingStateAsync(appId, cancellationToken).ConfigureAwait(false);
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
