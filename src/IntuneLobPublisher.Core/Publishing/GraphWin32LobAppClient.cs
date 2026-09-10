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
/// one built by <see cref="GraphClientFactory"/> (authentication + retry already wired). Builds an
/// absolute <c>/beta/</c> path rather than relying on the client's <c>/v1.0/</c> base address, the
/// same technique <see cref="GraphMacOsAppClient"/> uses: <c>win32LobApp</c>'s <c>displayVersion</c>
/// and <c>roleScopeTagIds</c> properties only exist on the beta resource (doc/adr/publishing.md
/// 2026-09-10 entry), so every call for this resource stays on beta.
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
        const string requestUri = "/beta/deviceAppManagement/mobileApps";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a created app without an id for '{requestUri}'.");
    }

    public async Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken)
    {
        var requestUri = $"/beta/deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(payload), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    private sealed class MobileAppResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
