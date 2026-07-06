using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>
/// Downloads blobs with <see cref="DefaultAzureCredential"/> (workload identity in CI). Retry is
/// delegated to the Azure SDK's built-in pipeline (request and range-download failures alike),
/// so this path intentionally does not use <see cref="DownloadRetryPolicy"/>.
/// </summary>
public sealed partial class AzureBlobDownloader : IAzureBlobDownloader
{
    private static readonly BlobClientOptions ClientOptions = new()
    {
        Retry =
        {
            Mode = RetryMode.Exponential,
            MaxRetries = 5,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(60),
        },
    };

    private readonly object _clientLock = new();
    private BlobServiceClient? _client;
    private string? _clientAccountName;

    public async Task DownloadToAsync(
        string accountName, string container, string blobName, string destinationPath,
        CancellationToken cancellationToken)
    {
        var blobClient = GetServiceClient(accountName)
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        await blobClient.DownloadToAsync(destinationPath, cancellationToken).ConfigureAwait(false);
    }

    private BlobServiceClient GetServiceClient(string accountName)
    {
        ValidateAccountName(accountName);

        lock (_clientLock)
        {
            // Manifests overwhelmingly use a single account; cache the last client.
            if (_client is null || !string.Equals(_clientAccountName, accountName, StringComparison.Ordinal))
            {
                _client = new BlobServiceClient(
                    new Uri($"https://{accountName}.blob.core.windows.net"),
                    new DefaultAzureCredential(),
                    ClientOptions);
                _clientAccountName = accountName;
            }

            return _client;
        }
    }

    /// <summary>
    /// The account name becomes a hostname, so an invalid value could redirect the download to an
    /// attacker-controlled host. Enforce the Azure storage account naming rules.
    /// </summary>
    internal static void ValidateAccountName(string accountName)
    {
        if (!AccountNameRegex().IsMatch(accountName))
        {
            throw new SourceDownloadException(
                $"AccountName '{accountName}' is not a valid Azure storage account name "
                + "(3-24 lowercase letters and digits).");
        }
    }

    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex AccountNameRegex();
}
