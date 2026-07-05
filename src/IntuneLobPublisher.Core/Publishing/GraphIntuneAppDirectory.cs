using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Lists Intune mobile apps via <c>GET /deviceAppManagement/mobileApps</c>, following
/// <c>@odata.nextLink</c> pages. Uses the caller-supplied <see cref="HttpClient"/>, which is
/// expected to be one built by <see cref="GraphClientFactory"/> (authentication + retry already wired).
/// </summary>
public sealed class GraphIntuneAppDirectory : IIntuneAppDirectory
{
    private const string InitialRequestUri = "deviceAppManagement/mobileApps?$select=id,displayName,notes";

    private readonly HttpClient _httpClient;

    public GraphIntuneAppDirectory(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<IntuneAppSummary>> ListAppsAsync(CancellationToken cancellationToken)
    {
        var results = new List<IntuneAppSummary>();
        string? requestUri = InitialRequestUri;

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new GraphRequestException(
                    $"Failed to list Intune mobile apps: GET '{requestUri}' returned {(int)response.StatusCode}.",
                    (int)response.StatusCode,
                    GetHeader(response, "client-request-id"),
                    GetHeader(response, "request-id"));
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var page = await JsonSerializer.DeserializeAsync<MobileAppListPage>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new GraphRequestException($"Graph returned an empty body for '{requestUri}'.", (int)response.StatusCode, null, null);

            foreach (var app in page.Value)
            {
                results.Add(new IntuneAppSummary(app.Id, app.DisplayName, app.Notes));
            }

            requestUri = page.NextLink;
        }

        return results;
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private sealed class MobileAppListPage
    {
        [JsonPropertyName("value")]
        public List<MobileAppEntry> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed class MobileAppEntry
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("notes")]
        public string? Notes { get; init; }
    }
}
