using System.Text;
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs.Specialized;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>A refreshed SAS URI and its new expiration, returned after calling Graph's <c>renewUpload</c> action.</summary>
public sealed record SasUriRenewal(Uri Uri, DateTimeOffset ExpiresAt);

public interface IAzureStorageBlockBlobUploader
{
    /// <summary>
    /// Uploads <paramref name="content"/> to <paramref name="sasUri"/> as a block blob, in
    /// <see cref="ContentUploadOptions.BlockSizeBytes"/>-sized chunks. Before staging each block, and
    /// once more before the final Put Block List call, if the time remaining before
    /// <paramref name="expiresAt"/> has dropped below <see cref="ContentUploadOptions.RenewalSafetyMargin"/>,
    /// calls <paramref name="renewAsync"/> (which is expected to call the Graph <c>renewUpload</c> action
    /// and re-read the file's SAS URI) and continues with the refreshed URI/expiry.
    /// </summary>
    /// <exception cref="ContentUploadRejectedException">
    /// Azure Storage rejected a stage or commit call and issue #150's SAS-authentication-403 recovery
    /// (see <see cref="AzureStorageBlockBlobUploader"/>) could not recover it within its own deadline.
    /// </exception>
    Task UploadAsync(
        Uri sasUri,
        DateTimeOffset expiresAt,
        Stream content,
        Func<CancellationToken, Task<SasUriRenewal>> renewAsync,
        ContentUploadOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uploads the encrypted <c>.intunewin</c> payload to Azure Storage using
/// <see cref="BlockBlobClient"/>'s low-level stage/commit block operations rather than the SDK's
/// automatic partitioned upload, because <c>renewUpload</c> must be interleaved between chunks when a
/// long-running upload approaches SAS expiry.
///
/// issue #150: Intune's <c>azureStorageUri</c> is a service SAS scoped to a stored access policy (its
/// <c>si=</c> signed identifier). Azure Storage documents that creating or updating such a policy can
/// take up to ~30 seconds to propagate, and a request against a SAS tied to a not-yet-propagated policy
/// fails with 403 <c>AuthenticationFailed</c> / "SAS identifier cannot be found for specified signed
/// identifier" in the interim (https://learn.microsoft.com/rest/api/storageservices/define-stored-access-policy).
/// This was observed in production: a second package's upload failed ~3 seconds after its content file
/// was created, while a first package in the same run had taken ~30 seconds to reach the same call and
/// had not hit it. The cause is not fully confirmed (the evidence is consistent with propagation delay,
/// but does not rule out Intune revoking or rotating the policy), so <see cref="StageOrCommitAsync"/>
/// recovers from the failure without asserting which explanation is correct.
/// </summary>
public sealed class AzureStorageBlockBlobUploader : IAzureStorageBlockBlobUploader
{
    private const string AuthenticationFailedErrorCode = "AuthenticationFailed";

    private readonly TimeProvider _timeProvider;
    private readonly Azure.Storage.Blobs.BlobClientOptions? _clientOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly ILogger<AzureStorageBlockBlobUploader> _logger;

    public AzureStorageBlockBlobUploader(
        TimeProvider? timeProvider = null,
        Azure.Storage.Blobs.BlobClientOptions? clientOptions = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ILogger<AzureStorageBlockBlobUploader>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientOptions = clientOptions;
        _delayAsync = delayAsync ?? Task.Delay;
        _logger = logger ?? NullLogger<AzureStorageBlockBlobUploader>.Instance;
    }

    public async Task UploadAsync(
        Uri sasUri,
        DateTimeOffset expiresAt,
        Stream content,
        Func<CancellationToken, Task<SasUriRenewal>> renewAsync,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
    {
        if (options.BlockSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.BlockSizeBytes, $"{nameof(ContentUploadOptions.BlockSizeBytes)} must be positive.");
        }

        var blockBlobClient = new BlockBlobClient(sasUri, _clientOptions);
        var buffer = new byte[options.BlockSizeBytes];
        var blockIds = new List<string>();
        var blockIndex = 0;

        while (true)
        {
            var bytesRead = await ReadFullBlockAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            (blockBlobClient, expiresAt) = await RenewIfNeededAsync(
                blockBlobClient, expiresAt, options, renewAsync, cancellationToken).ConfigureAwait(false);

            var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(blockIndex.ToString("D6")));

            // Captured by value so a retry inside StageOrCommitAsync rebuilds a fresh MemoryStream from
            // the same bytes rather than resending an already-consumed one (issue #150: the original
            // stream's read position advances on send, so reusing the instance across attempts would
            // stage an empty or truncated block on the second try).
            var blockBuffer = buffer;
            var blockLength = bytesRead;

            (blockBlobClient, expiresAt) = await StageOrCommitAsync(
                    "stageBlock",
                    blockBlobClient,
                    expiresAt,
                    (client, ct) => StageBlockAsync(client, blockId, blockBuffer, blockLength, ct),
                    renewAsync,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);

            blockIds.Add(blockId);
            blockIndex++;
        }

        // Re-check right before the final Put Block List call too, not just before each staged block:
        // the last StageBlockAsync can by itself consume most of the remaining safety margin (or all of
        // it, on a slow connection), and a commit against an already-expired SAS loses the entire upload
        // with no retry path.
        (blockBlobClient, expiresAt) = await RenewIfNeededAsync(
            blockBlobClient, expiresAt, options, renewAsync, cancellationToken).ConfigureAwait(false);

        await StageOrCommitAsync(
                "commitBlockList",
                blockBlobClient,
                expiresAt,
                (client, ct) => client.CommitBlockListAsync(blockIds, cancellationToken: ct),
                renewAsync,
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Must be `async`/`await`, not a non-async method returning the inner Task directly: with a
    // non-async method, `using`'s Dispose() runs in the caller's synchronous continuation right after
    // StageBlockAsync is *called* (i.e. once it hands back a Task), not after its actual I/O completes,
    // so `chunk` could be disposed while the SDK is still reading from it (Copilot review, PR #151).
    private static async Task StageBlockAsync(BlockBlobClient client, string blockId, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        using var chunk = new MemoryStream(buffer, 0, length, writable: false);
        await client.StageBlockAsync(blockId, chunk, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="operation"/> once; on a non-SAS-authentication <see cref="RequestFailedException"/>
    /// translates and rethrows immediately (no retry - e.g. 404 <c>BlobNotFound</c>, 400 <c>InvalidBlockList</c>).
    /// On a 403 <c>AuthenticationFailed</c>, enters <see cref="RecoverFromSasAuthenticationFailureAsync"/>.
    /// Returns the (possibly renewed) client/expiry so the caller's next block sees the current SAS.
    /// </summary>
    private async Task<(BlockBlobClient Client, DateTimeOffset ExpiresAt)> StageOrCommitAsync(
        string stage,
        BlockBlobClient blockBlobClient,
        DateTimeOffset expiresAt,
        Func<BlockBlobClient, CancellationToken, Task> operation,
        Func<CancellationToken, Task<SasUriRenewal>> renewAsync,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
    {
        RequestFailedException firstFailure;
        try
        {
            await operation(blockBlobClient, cancellationToken).ConfigureAwait(false);
            return (blockBlobClient, expiresAt);
        }
        catch (RequestFailedException ex) when (!IsSasAuthenticationFailure(ex))
        {
            throw ToContentUploadRejectedException(stage, ex);
        }
        catch (RequestFailedException ex)
        {
            firstFailure = ex;
        }

        return await RecoverFromSasAuthenticationFailureAsync(
                stage, firstFailure, blockBlobClient, expiresAt, operation, renewAsync, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers a SAS-authentication 403 for one stage/commit call (issue #150). Branches on remaining
    /// SAS lifetime first: an already-expired or near-expiry SAS cannot recover by waiting, so recovery
    /// goes straight to a renewal ("Phase B"); otherwise it retries the *same* SAS across a window
    /// documented to exceed Azure Storage's stored access policy propagation delay ("Phase A"), falling
    /// back to a bounded number of <c>renewUpload</c> calls if Phase A does not converge - a fresh SAS may
    /// re-create the policy and need the same propagation wait, so each renewal restarts Phase A rather
    /// than retrying instantly. The whole recovery (both phases, however many renewals) is bounded by one
    /// deadline fixed here and enforced with a token linked to the caller's, so a timeout can be told apart
    /// from the caller's own cancellation and only the former turns into a failure of this entry.
    /// </summary>
    private async Task<(BlockBlobClient Client, DateTimeOffset ExpiresAt)> RecoverFromSasAuthenticationFailureAsync(
        string stage,
        RequestFailedException lastFailure,
        BlockBlobClient blockBlobClient,
        DateTimeOffset expiresAt,
        Func<BlockBlobClient, CancellationToken, Task> operation,
        Func<CancellationToken, Task<SasUriRenewal>> renewAsync,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(options.SasActivationTimeout, _timeProvider);
        using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var recoveryToken = recoveryCts.Token;
        var recoveryStart = _timeProvider.GetUtcNow();
        var attempt = 0;
        var renewals = 0;

        LogRecoveryStart(stage, lastFailure, expiresAt);

        try
        {
            while (true)
            {
                var nearExpiry = expiresAt - _timeProvider.GetUtcNow() < options.RenewalSafetyMargin;

                if (!nearExpiry)
                {
                    var sameSasDeadline = _timeProvider.GetUtcNow() + options.SasActivationSameSasWindow;
                    while (true)
                    {
                        await _delayAsync(options.SasActivationRetryDelay, recoveryToken).ConfigureAwait(false);
                        attempt++;

                        try
                        {
                            await operation(blockBlobClient, recoveryToken).ConfigureAwait(false);
                            LogRecoverySucceeded(stage, attempt, renewals, recoveryStart);
                            return (blockBlobClient, expiresAt);
                        }
                        catch (RequestFailedException ex) when (!IsSasAuthenticationFailure(ex))
                        {
                            throw ToContentUploadRejectedException(stage, ex);
                        }
                        catch (RequestFailedException ex)
                        {
                            lastFailure = ex;
                            LogRecoveryRetry(stage, attempt, renewals, recoveryStart, expiresAt, ex);
                        }

                        if (expiresAt - _timeProvider.GetUtcNow() < options.RenewalSafetyMargin)
                        {
                            // The wait itself carried the SAS into its renewal margin: stop retrying the
                            // same SAS and fall through to a renewal below.
                            break;
                        }

                        if (_timeProvider.GetUtcNow() >= sameSasDeadline)
                        {
                            // Same-SAS window exhausted without recovering: a renewal is the only
                            // remaining option, not a longer wait on the same signature.
                            break;
                        }
                    }
                }

                if (renewals >= options.SasActivationMaxRenewals)
                {
                    throw ToContentUploadRejectedException(stage, lastFailure);
                }

                renewals++;
                var renewal = await renewAsync(recoveryToken).ConfigureAwait(false);
                blockBlobClient = new BlockBlobClient(renewal.Uri, _clientOptions);
                expiresAt = renewal.ExpiresAt;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // recoveryToken tripped because the recovery deadline elapsed, not because the caller
            // cancelled: this upload step failed, but the caller's own cancellation is untouched, so the
            // batch is not aborted - only this manifest entry is.
            throw ToContentUploadRejectedException(stage, lastFailure);
        }
    }

    private async Task<(BlockBlobClient Client, DateTimeOffset ExpiresAt)> RenewIfNeededAsync(
        BlockBlobClient blockBlobClient,
        DateTimeOffset expiresAt,
        ContentUploadOptions options,
        Func<CancellationToken, Task<SasUriRenewal>> renewAsync,
        CancellationToken cancellationToken)
    {
        if (expiresAt - _timeProvider.GetUtcNow() >= options.RenewalSafetyMargin)
        {
            return (blockBlobClient, expiresAt);
        }

        var renewal = await renewAsync(cancellationToken).ConfigureAwait(false);
        return (new BlockBlobClient(renewal.Uri, _clientOptions), renewal.ExpiresAt);
    }

    private static bool IsSasAuthenticationFailure(RequestFailedException ex)
        => ex.Status == 403 && string.Equals(ex.ErrorCode, AuthenticationFailedErrorCode, StringComparison.Ordinal);

    /// <summary>
    /// Translates a Storage <see cref="RequestFailedException"/> into a <see cref="ContentUploadRejectedException"/>
    /// carrying only status/error-code/detail/request-id. The original exception is deliberately not kept
    /// as an inner exception: doing so would make "never log a signed URL" (AGENTS.md) depend on what a
    /// future Azure SDK version happens to put in <c>RequestFailedException.Message</c> or <c>ToString()</c>,
    /// rather than on this repository's own, reviewable message. The only call site is this file, so the
    /// lost stack trace has little diagnostic value; <see cref="LogRecoveryRetry"/> already records the
    /// storage request id for correlating with storage-side logs.
    /// </summary>
    private static ContentUploadRejectedException ToContentUploadRejectedException(string stage, RequestFailedException ex)
        => new(stage, ex.Status, ex.ErrorCode, TryGetAuthenticationErrorDetail(ex), TryGetStorageRequestId(ex));

    private static string? TryGetStorageRequestId(RequestFailedException ex)
    {
        try
        {
            return ex.GetRawResponse()?.Headers.RequestId;
        }
        catch
        {
            // Best-effort diagnostics only; never let reading extra detail mask the real failure.
            return null;
        }
    }

    // The stored access policy hint ("SAS identifier cannot be found for specified signed identifier",
    // issue #150) lives only in the storage error body's <AuthenticationErrorDetail> element, not as a
    // structured RequestFailedException property. Best-effort: any parse failure yields null rather than
    // masking the real failure with a secondary exception.
    private static string? TryGetAuthenticationErrorDetail(RequestFailedException ex)
    {
        try
        {
            var response = ex.GetRawResponse();
            var content = response?.Content;
            if (content is null || content.IsEmpty)
            {
                return null;
            }

            using var stream = content.ToStream();
            var root = XDocument.Load(stream).Root;
            return root?.Element("AuthenticationErrorDetail")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private void LogRecoveryStart(string stage, RequestFailedException ex, DateTimeOffset expiresAt)
        => _logger.LogWarning(
            "Azure Storage rejected content upload step '{Stage}' with 403 {ErrorCode} (request id {RequestId}); " +
            "{SecondsToExpiry}s remain before SAS expiry. This can happen when the stored access policy the SAS " +
            "relies on has not finished propagating yet (issue #150) or for other authentication reasons; recovering.",
            stage, ex.ErrorCode, TryGetStorageRequestId(ex), (expiresAt - _timeProvider.GetUtcNow()).TotalSeconds);

    private void LogRecoveryRetry(
        string stage, int attempt, int renewals, DateTimeOffset recoveryStart, DateTimeOffset expiresAt, RequestFailedException ex)
        => _logger.LogWarning(
            "Content upload step '{Stage}' retry {Attempt} (renewals so far: {Renewals}) still failing after " +
            "{ElapsedSeconds:F1}s with 403 {ErrorCode} (request id {RequestId}); {SecondsToExpiry}s remain before SAS expiry.",
            stage, attempt, renewals, (_timeProvider.GetUtcNow() - recoveryStart).TotalSeconds,
            ex.ErrorCode, TryGetStorageRequestId(ex), (expiresAt - _timeProvider.GetUtcNow()).TotalSeconds);

    private void LogRecoverySucceeded(string stage, int attempt, int renewals, DateTimeOffset recoveryStart)
        => _logger.LogInformation(
            "Content upload step '{Stage}' recovered after {Attempt} retr{Suffix} and {Renewals} renewal(s) in {ElapsedSeconds:F1}s.",
            stage, attempt, attempt == 1 ? "y" : "ies", renewals, (_timeProvider.GetUtcNow() - recoveryStart).TotalSeconds);

    // Stream.ReadAsync may return short reads before EOF (e.g. over a decompressing ZipArchiveEntry
    // stream), so block boundaries must be filled explicitly rather than trusting a single read call.
    private static async Task<int> ReadFullBlockAsync(Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await content.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
