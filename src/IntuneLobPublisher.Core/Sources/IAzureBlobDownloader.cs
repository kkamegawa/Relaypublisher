namespace IntuneLobPublisher.Core.Sources;

/// <summary>
/// Seam over Azure.Storage.Blobs so <see cref="AzureBlobSourceProvider"/> is unit-testable
/// without a mocking library (same idiom as IProcessRunner).
/// </summary>
public interface IAzureBlobDownloader
{
    /// <summary>Downloads one blob to <paramref name="destinationPath"/>, overwriting it.</summary>
    /// <exception cref="Azure.RequestFailedException">The blob service rejected the request.</exception>
    Task DownloadToAsync(
        string accountName,
        string container,
        string blobName,
        string destinationPath,
        CancellationToken cancellationToken);
}
