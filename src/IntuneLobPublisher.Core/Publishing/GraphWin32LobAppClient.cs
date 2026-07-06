using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

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
        var body = await ReadJsonAsync<MobileAppResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw new GraphRequestException(
            $"Graph returned a created app without an id for '{requestUri}'.",
            (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
    }

    public async Task UpdateAppAsync(string appId, Win32LobAppPayload payload, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(payload), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

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
