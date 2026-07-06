using System.Net.Http.Headers;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>
/// Downloads GitHub Release assets (Type: githubRelease), including assets of private repositories.
/// Always goes through the REST asset endpoint because `browser_download_url` does not work for
/// private repositories; the same flow also works for public ones.
/// </summary>
public sealed class GitHubReleaseSourceProvider : ISourceProvider
{
    private const string ApiBaseUrl = "https://api.github.com";

    private readonly HttpClient _httpClient;
    private readonly DownloadRetryPolicy _retryPolicy;
    private readonly ILogger<GitHubReleaseSourceProvider> _logger;
    private readonly Func<string, string?> _environment;

    public GitHubReleaseSourceProvider(
        HttpClient httpClient,
        DownloadRetryPolicy retryPolicy,
        ILogger<GitHubReleaseSourceProvider> logger)
        : this(httpClient, retryPolicy, logger, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test constructor allowing environment variable lookups to be stubbed.</summary>
    public GitHubReleaseSourceProvider(
        HttpClient httpClient,
        DownloadRetryPolicy retryPolicy,
        ILogger<GitHubReleaseSourceProvider> logger,
        Func<string, string?> environment)
    {
        _httpClient = httpClient;
        _retryPolicy = retryPolicy;
        _logger = logger;
        _environment = environment;
    }

    public string SourceType => "githubRelease";

    public async Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken)
    {
        var source = request.Source;
        if (source.Owner is null || source.Repository is null || source.Tag is null || source.AssetName is null)
        {
            throw new SourceDownloadException(
                "githubRelease source requires Owner, Repository, Tag and AssetName.");
        }

        var token = ResolveToken(source.Auth);
        var repoDescription = $"{source.Owner}/{source.Repository}@{source.Tag}";

        _logger.LogInformation(
            "Downloading GitHub release asset {AssetName} from {Owner}/{Repository} tag {Tag} to {Destination}",
            source.AssetName, source.Owner, source.Repository, source.Tag, request.DestinationPath);

        var assetId = await GetAssetIdAsync(source, token, repoDescription, cancellationToken).ConfigureAwait(false);
        await DownloadAssetAsync(source, token, assetId, repoDescription, request.DestinationPath, cancellationToken)
            .ConfigureAwait(false);

        var size = new FileInfo(request.DestinationPath).Length;
        var sha256 = await ChecksumVerifier.ComputeSha256Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        return new DownloadedFile(request.DestinationPath, size, sha256);
    }

    /// <summary>
    /// Resolves the token per the manifest Auth block: `token` reads the environment variable named
    /// by SecretName, `none`/absent downloads unauthenticated (public repositories).
    /// </summary>
    private string? ResolveToken(Manifests.AuthManifest? auth)
    {
        switch (auth?.Type)
        {
            case null or "none":
                return null;
            case "token":
                if (string.IsNullOrEmpty(auth.SecretName))
                {
                    throw new SourceDownloadException(
                        "githubRelease source has Auth.Type 'token' but no Auth.SecretName.");
                }

                var token = _environment(auth.SecretName);
                if (string.IsNullOrEmpty(token))
                {
                    throw new SourceDownloadException(
                        $"Environment variable '{auth.SecretName}' (Auth.SecretName) is not set or empty. "
                        + "Map the repository secret to this variable on the CI packaging job; "
                        + "note that fork pull requests do not receive secrets.");
                }

                return token;
            default:
                throw new SourceDownloadException(
                    $"githubRelease does not support Auth.Type '{auth.Type}'. Use 'token' or 'none'.");
        }
    }

    private async Task<long> GetAssetIdAsync(
        Manifests.SourceManifest source, string? token, string repoDescription, CancellationToken cancellationToken)
    {
        var releaseUrl = $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(source.Owner!)}/{Uri.EscapeDataString(source.Repository!)}"
            + $"/releases/tags/{Uri.EscapeDataString(source.Tag!)}";

        try
        {
            return await _retryPolicy.ExecuteAsync($"release metadata for {repoDescription}", async ct =>
            {
                using var request = CreateRequest(releaseUrl, token, "application/vnd.github+json");
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var payload = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var release = await JsonDocument.ParseAsync(payload, cancellationToken: ct).ConfigureAwait(false);
                return FindAssetId(release, source.AssetName!, repoDescription);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceDownloadException(
                $"Failed to query GitHub release {repoDescription}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new SourceDownloadException(
                $"GitHub release query for {repoDescription} returned invalid JSON: {ex.Message}", ex);
        }
    }

    private static long FindAssetId(JsonDocument release, string assetName, string repoDescription)
    {
        var availableNames = new List<string>();
        if (release.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
                if (name is null)
                {
                    continue;
                }

                if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("id", out var id))
                {
                    return id.GetInt64();
                }

                availableNames.Add(name);
            }
        }

        // Asset names are not secrets; listing them makes a typo in AssetName immediately actionable.
        throw new SourceDownloadException(
            $"Release {repoDescription} has no asset named '{assetName}'. "
            + (availableNames.Count > 0
                ? $"Available assets: {string.Join(", ", availableNames)}."
                : "The release has no assets."));
    }

    private async Task DownloadAssetAsync(
        Manifests.SourceManifest source, string? token, long assetId, string repoDescription,
        string destinationPath, CancellationToken cancellationToken)
    {
        var assetUrl = $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(source.Owner!)}/{Uri.EscapeDataString(source.Repository!)}"
            + $"/releases/assets/{assetId}";

        try
        {
            await _retryPolicy.ExecuteAsync($"asset {source.AssetName} of {repoDescription}", async ct =>
            {
                // GitHub answers 302 to a pre-signed storage URL; HttpClient follows it and drops the
                // Authorization header on the cross-origin redirect, which is what the signed URL requires.
                // Never log the redirected URI - it embeds a signature.
                using var request = CreateRequest(assetUrl, token, "application/octet-stream");
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var file = File.Create(destinationPath);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SourceDownloadException(
                $"Failed to download asset '{source.AssetName}' of {repoDescription}: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new SourceDownloadException(
                $"Failed to save asset '{source.AssetName}' of {repoDescription} to '{destinationPath}': {ex.Message}", ex);
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string? token, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The GitHub API rejects requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Relaypublisher", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (token is not null)
        {
            // Per-request header only; the HttpClient singleton is shared with other components.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
