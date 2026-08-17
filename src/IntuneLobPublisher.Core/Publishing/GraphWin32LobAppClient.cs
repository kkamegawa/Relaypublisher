using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls that create and update the <c>win32LobApp</c> resource itself
/// (doc/02-dotnet-architecture.md §7.2 "win32LobApp create / update"). Content upload and
/// assignment calls live in their own clients.
/// </summary>
public interface IWin32LobAppClient
{
    /// <summary>Creates a new <c>win32LobApp</c> and returns the created app id.</summary>
    Task<string> CreateAppAsync(Win32LobAppPayload payload, CancellationToken cancellationToken);

    /// <summary>Patches an existing <c>win32LobApp</c> with the full mapped payload.</summary>
    Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>, which is expected to be
/// one built by <see cref="GraphClientFactory"/> (authentication + retry already wired). Follows the
/// same structure as <see cref="GraphMobileAppContentClient"/>.
/// </summary>
public sealed class GraphWin32LobAppClient : IWin32LobAppClient
{
    private readonly HttpClient _httpClient;

    public GraphWin32LobAppClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> CreateAppAsync(Win32LobAppPayload payload, CancellationToken cancellationToken)
    {
        const string requestUri = "deviceAppManagement/mobileApps";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a created app without an id for '{requestUri}'.");
    }

    public async Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(payload), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    private sealed class MobileAppResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
