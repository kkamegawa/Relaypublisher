using System.Text;
using Azure.Storage.Blobs.Specialized;

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
/// </summary>
public sealed class AzureStorageBlockBlobUploader : IAzureStorageBlockBlobUploader
{
    private readonly TimeProvider _timeProvider;
    private readonly Azure.Storage.Blobs.BlobClientOptions? _clientOptions;

    public AzureStorageBlockBlobUploader(TimeProvider? timeProvider = null, Azure.Storage.Blobs.BlobClientOptions? clientOptions = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientOptions = clientOptions;
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

        while (true)
        {
            var bytesRead = await ReadFullBlockAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            (blockBlobClient, expiresAt) = await RenewIfNeededAsync(
                blockBlobClient, expiresAt, options, renewAsync, cancellationToken).ConfigureAwait(false);

            var blockId = Convert.ToBase64String(Encoding.UTF8.GetBytes(blockIds.Count.ToString("D6")));
            using (var chunk = new MemoryStream(buffer, 0, bytesRead, writable: false))
            {
                await blockBlobClient.StageBlockAsync(blockId, chunk, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            blockIds.Add(blockId);
        }

        // Re-check right before the final Put Block List call too, not just before each staged block:
        // the last StageBlockAsync can by itself consume most of the remaining safety margin (or all of
        // it, on a slow connection), and a commit against an already-expired SAS loses the entire upload
        // with no retry path.
        (blockBlobClient, _) = await RenewIfNeededAsync(
            blockBlobClient, expiresAt, options, renewAsync, cancellationToken).ConfigureAwait(false);

        await blockBlobClient.CommitBlockListAsync(blockIds, cancellationToken: cancellationToken).ConfigureAwait(false);
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
