using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>Downloads unauthenticated public HTTP sources (Type: publicHttp).</summary>
public sealed class PublicHttpSourceProvider : ISourceProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PublicHttpSourceProvider> _logger;

    public PublicHttpSourceProvider(HttpClient httpClient, ILogger<PublicHttpSourceProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string SourceType => "publicHttp";

    public async Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken)
    {
        var source = request.Source;
        if (source.Url is null)
        {
            throw new SourceDownloadException("publicHttp source has no Url.");
        }

        if (source.Auth?.Type is not (null or "none"))
        {
            throw new SourceDownloadException(
                $"publicHttp does not support Auth.Type '{source.Auth.Type}'. Use githubRelease or azureBlob for authenticated sources.");
        }

        _logger.LogInformation("Downloading {Url} to {Destination}", source.Url, request.DestinationPath);

        try
        {
            using var response = await _httpClient.GetAsync(
                source.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await using (var file = File.Create(request.DestinationPath))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new SourceDownloadException($"Failed to download '{source.Url}': {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new SourceDownloadException($"Failed to save download to '{request.DestinationPath}': {ex.Message}", ex);
        }

        var size = new FileInfo(request.DestinationPath).Length;
        var sha256 = await ChecksumVerifier.ComputeSha256Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        return new DownloadedFile(request.DestinationPath, size, sha256);
    }
}
