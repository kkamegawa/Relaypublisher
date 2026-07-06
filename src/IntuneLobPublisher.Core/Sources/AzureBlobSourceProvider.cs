using Azure;
using Azure.Identity;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>
/// Downloads Azure Blob sources (Type: azureBlob) using federated identity. Only
/// <c>Auth.Type: workloadIdentity</c> is supported; anonymously readable blobs should use
/// publicHttp instead.
/// </summary>
public sealed class AzureBlobSourceProvider : ISourceProvider
{
    private readonly IAzureBlobDownloader _downloader;
    private readonly ILogger<AzureBlobSourceProvider> _logger;

    public AzureBlobSourceProvider(IAzureBlobDownloader downloader, ILogger<AzureBlobSourceProvider> logger)
    {
        _downloader = downloader;
        _logger = logger;
    }

    public string SourceType => "azureBlob";

    public async Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken)
    {
        var source = request.Source;
        if (source.AccountName is null || source.Container is null || source.BlobName is null)
        {
            throw new SourceDownloadException(
                "azureBlob source requires AccountName, Container and BlobName.");
        }

        switch (source.Auth?.Type)
        {
            case "workloadIdentity":
                break;
            case null or "none":
                throw new SourceDownloadException(
                    "azureBlob requires Auth.Type 'workloadIdentity'. "
                    + "Use publicHttp for anonymously readable URLs.");
            default:
                throw new SourceDownloadException(
                    $"azureBlob does not support Auth.Type '{source.Auth.Type}'. Use 'workloadIdentity'.");
        }

        // Log names only - never blob URIs, which could carry SAS signatures in other contexts.
        _logger.LogInformation(
            "Downloading blob {Container}/{BlobName} from account {AccountName} to {Destination}",
            source.Container, source.BlobName, source.AccountName, request.DestinationPath);

        var blobDescription = $"blob '{source.Container}/{source.BlobName}' from account '{source.AccountName}'";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await _downloader.DownloadToAsync(
                source.AccountName, source.Container, source.BlobName, request.DestinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            var hint = ex.Status switch
            {
                403 => " Check that the federated identity has the 'Storage Blob Data Reader' role on the container or account.",
                404 => " Check that the container and blob path exist.",
                _ => string.Empty,
            };
            var errorCode = string.IsNullOrEmpty(ex.ErrorCode) ? "unknown" : ex.ErrorCode;
            throw new SourceDownloadException(
                $"Failed to download {blobDescription} (status {ex.Status}, code {errorCode}).{hint}", ex);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new SourceDownloadException(
                $"No Azure credential is available to download {blobDescription}. "
                + "The CI job needs an Azure login with workload identity (e.g. azure/login with id-token: write).", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new SourceDownloadException(
                $"Azure authentication failed while downloading {blobDescription}. "
                + "The CI job needs an Azure login with workload identity (e.g. azure/login with id-token: write); "
                + "verify the federated credential configuration of the CI identity.", ex);
        }
        catch (IOException ex)
        {
            throw new SourceDownloadException(
                $"Failed to save {blobDescription} to '{request.DestinationPath}': {ex.Message}", ex);
        }

        var size = new FileInfo(request.DestinationPath).Length;
        var sha256 = await ChecksumVerifier.ComputeSha256Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        return new DownloadedFile(request.DestinationPath, size, sha256);
    }
}
