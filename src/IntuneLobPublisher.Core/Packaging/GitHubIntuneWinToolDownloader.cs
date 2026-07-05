using System.Net.Http.Headers;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Downloads IntuneWinAppUtil.exe from the official microsoft/Microsoft-Win32-Content-Prep-Tool
/// GitHub repository. Uses the unauthenticated public API; no token is sent or logged.
/// </summary>
public sealed class GitHubIntuneWinToolDownloader : IIntuneWinToolDownloader
{
    private const string RepositoryOwner = "microsoft";
    private const string RepositoryName = "Microsoft-Win32-Content-Prep-Tool";
    private const string ToolFileName = "IntuneWinAppUtil.exe";
    private const string ApiBaseUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}";
    private const string RawBaseUrl = $"https://raw.githubusercontent.com/{RepositoryOwner}/{RepositoryName}";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubIntuneWinToolDownloader> _logger;

    public GitHubIntuneWinToolDownloader(HttpClient httpClient, ILogger<GitHubIntuneWinToolDownloader> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        using var release = await GetReleaseAsync($"{ApiBaseUrl}/releases/latest", cancellationToken).ConfigureAwait(false);
        var tag = release.RootElement.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new PackagingException(
                $"Latest release of {RepositoryOwner}/{RepositoryName} has no tag_name.");
        }

        _logger.LogInformation("Latest {Repository} release is {Tag}", RepositoryName, tag);
        return tag;
    }

    public async Task DownloadAsync(string version, string destinationPath, CancellationToken cancellationToken)
    {
        // Prefer a release asset when one exists; the repository historically ships the
        // binary committed at the repo root instead, so fall back to the raw file at the tag.
        var downloadUrl = await TryGetAssetUrlAsync(version, cancellationToken).ConfigureAwait(false)
            ?? $"{RawBaseUrl}/{version}/{ToolFileName}";

        _logger.LogInformation("Downloading {ToolFileName} {Version} from {Url}", ToolFileName, version, downloadUrl);
        try
        {
            using var request = CreateRequest(downloadUrl);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var file = File.Create(destinationPath);
            await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PackagingException(
                $"Failed to download {ToolFileName} {version} from '{downloadUrl}': {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new PackagingException(
                $"Failed to save {ToolFileName} {version} to '{destinationPath}': {ex.Message}", ex);
        }
    }

    private async Task<string?> TryGetAssetUrlAsync(string version, CancellationToken cancellationToken)
    {
        JsonDocument release;
        try
        {
            release = await GetReleaseAsync($"{ApiBaseUrl}/releases/tags/{Uri.EscapeDataString(version)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackagingException)
        {
            // No release for this tag; the raw fallback may still work for a plain tag.
            return null;
        }

        using (release)
        {
            if (!release.RootElement.TryGetProperty("assets", out var assets))
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                if (string.Equals(name, ToolFileName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var url))
                {
                    return url.GetString();
                }
            }

            return null;
        }
    }

    private async Task<JsonDocument> GetReleaseAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(url);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PackagingException($"GitHub release query '{url}' failed: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new PackagingException($"GitHub release query '{url}' returned invalid JSON: {ex.Message}", ex);
        }
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The GitHub API rejects requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Relaypublisher", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }
}
