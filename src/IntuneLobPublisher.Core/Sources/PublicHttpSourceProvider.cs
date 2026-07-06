using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>Downloads unauthenticated public HTTP sources (Type: publicHttp).</summary>
public sealed class PublicHttpSourceProvider : ISourceProvider
{
    private readonly HttpClient _httpClient;
    private readonly DownloadRetryPolicy _retryPolicy;
    private readonly ILogger<PublicHttpSourceProvider> _logger;

    public PublicHttpSourceProvider(
        HttpClient httpClient, DownloadRetryPolicy retryPolicy, ILogger<PublicHttpSourceProvider> logger)
    {
        _httpClient = httpClient;
        _retryPolicy = retryPolicy;
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

        var safeUrl = RedactQuery(source.Url);
        _logger.LogInformation("Downloading {Url} to {Destination}", safeUrl, request.DestinationPath);

        try
        {
            await _retryPolicy.ExecuteAsync(safeUrl, async ct =>
            {
                using var response = await _httpClient.GetAsync(
                    source.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
                await using (var file = File.Create(request.DestinationPath))
                {
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceDownloadException($"Failed to download '{safeUrl}': {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new SourceDownloadException($"Failed to save download to '{request.DestinationPath}': {ex.Message}", ex);
        }

        var size = new FileInfo(request.DestinationPath).Length;
        var sha256 = await ChecksumVerifier.ComputeSha256Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        return new DownloadedFile(request.DestinationPath, size, sha256);
    }

    // Drops query string and fragment so signed URLs / embedded tokens never reach logs or
    // exception messages (AGENTS.md: never log secrets).
    private static string RedactQuery(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.GetLeftPart(UriPartial.Path)
            : url;
}
