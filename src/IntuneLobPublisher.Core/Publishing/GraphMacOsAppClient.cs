using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls that create and update a <c>macOSPkgApp</c> or <c>macOSLobApp</c> resource itself
/// (doc/00-overview.md §6.13). Content upload and assignment calls live in their own clients, same as
/// <see cref="GraphWin32LobAppClient"/>.
/// </summary>
public interface IMacOsAppClient
{
    /// <summary>Creates a new macOS app and returns the created app id.</summary>
    Task<string> CreateAppAsync(MacOsAppPayloadBase payload, CancellationToken cancellationToken);

    /// <summary>Patches an existing macOS app with the full mapped payload.</summary>
    Task UpdateAppAsync(string appId, MacOsAppPayloadBase payload, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>. Every macOS app call is
/// relative to the client's base address (Graph beta), whether the app is <c>macOSPkgApp</c>
/// (beta-only) or <c>macOSLobApp</c> (also on beta because <c>roleScopeTagIds</c> only exists there;
/// doc/adr/publishing.md 2026-09-10 entry). Payloads are serialized against their concrete runtime type
/// (<c>payload.GetType()</c>) so the derived pkg/lob-only properties are included - serializing against
/// the <see cref="MacOsAppPayloadBase"/> static type would silently drop them.
/// </summary>
public sealed class GraphMacOsAppClient : IMacOsAppClient
{
    private readonly HttpClient _httpClient;

    public GraphMacOsAppClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> CreateAppAsync(MacOsAppPayloadBase payload, CancellationToken cancellationToken)
    {
        const string requestUri = "deviceAppManagement/mobileApps";
        using var response = await _httpClient.PostAsync(requestUri, JsonContent.Create(payload, payload.GetType()), cancellationToken)
            .ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a created app without an id for '{requestUri}'.");
    }

    public async Task UpdateAppAsync(string appId, MacOsAppPayloadBase payload, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(payload, payload.GetType()), cancellationToken)
            .ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    private sealed class MobileAppResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
