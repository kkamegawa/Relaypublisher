using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls that create and update a <c>macOSPkgApp</c> or <c>macOSLobApp</c> resource itself
/// (doc/00-overview.md §6.13). Content upload and assignment calls live in their own clients, same as
/// <see cref="GraphWin32LobAppClient"/>.
/// </summary>
public interface IMacOsAppClient
{
    /// <summary>Creates a new macOS app and returns the created app id.</summary>
    Task<string> CreateAppAsync(MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Patches an existing macOS app with the full mapped payload.</summary>
    Task UpdateAppAsync(string appId, MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>. Builds absolute
/// <c>/v1.0/</c> or <c>/beta/</c> paths per call, the same technique
/// <see cref="Assignments.AssignmentGraphClient"/> and <see cref="GraphMobileAppContentClient"/> use:
/// <c>macOSPkgApp</c> is beta-only, so every call for a pkg app stays on <c>/beta/</c>, while
/// <c>macOSLobApp</c> (v1.0) stays on <c>/v1.0/</c>. Payloads are serialized against their concrete
/// runtime type (<c>payload.GetType()</c>) so the derived pkg/lob-only properties are included -
/// serializing against the <see cref="MacOsAppPayloadBase"/> static type would silently drop them.
/// </summary>
public sealed class GraphMacOsAppClient : IMacOsAppClient
{
    private readonly HttpClient _httpClient;

    public GraphMacOsAppClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> CreateAppAsync(MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{VersionSegment(useBeta)}/deviceAppManagement/mobileApps";
        using var response = await _httpClient.PostAsync(requestUri, JsonContent.Create(payload, payload.GetType()), cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw new GraphRequestException(
            $"Graph returned a created app without an id for '{requestUri}'.",
            (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
    }

    public async Task UpdateAppAsync(string appId, MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{VersionSegment(useBeta)}/deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(payload, payload.GetType()), cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    private static string VersionSegment(bool useBeta) => useBeta ? "/beta" : "/v1.0";

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
    {
        EnsureSuccess(response, requestUri);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        T? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new GraphRequestException(
                $"Graph returned a malformed body for '{requestUri}'.",
                (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
        }

        return body ?? throw new GraphRequestException(
            $"Graph returned an empty body for '{requestUri}'.",
            (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
    }

    private static void EnsureSuccess(HttpResponseMessage response, string requestUri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new GraphRequestException(
            $"Graph request to '{requestUri}' returned {(int)response.StatusCode}.",
            (int)response.StatusCode,
            GetHeader(response, "client-request-id"),
            GetHeader(response, "request-id"));
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed class MobileAppResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
