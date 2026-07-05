using System.Net.Http.Json;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Graph client for <c>/deviceAppManagement/mobileApps/{id}/assignments</c>. All calls go to the
/// beta endpoint because assignment filter properties do not exist on v1.0 targets
/// (doc/issues/issue-004-assignment-merge.md). Uses the caller-supplied <see cref="HttpClient"/>,
/// which is expected to be one built by <see cref="GraphClientFactory"/>, so authentication and
/// 429/503 `Retry-After` handling (issue-004 requirement) come from the existing pipeline.
/// </summary>
public sealed class GraphAppAssignmentClient : IGraphAppAssignmentClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _betaBaseAddress;

    public GraphAppAssignmentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _betaBaseAddress = GraphEndpoints.ToBeta(httpClient.BaseAddress
            ?? throw new ArgumentException("The Graph HttpClient must have a BaseAddress.", nameof(httpClient)));
    }

    public async Task<IReadOnlyList<CurrentAssignment>> GetAssignmentsAsync(string appId, CancellationToken cancellationToken)
    {
        var results = new List<CurrentAssignment>();
        Uri? requestUri = new(_betaBaseAddress, $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/assignments");

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, $"list assignments for app '{appId}'");

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            MobileAppAssignmentListPage? page;
            try
            {
                page = await JsonSerializer.DeserializeAsync<MobileAppAssignmentListPage>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new GraphRequestException($"Graph returned a malformed assignments body for app '{appId}'.", (int)response.StatusCode, null, null);
            }

            if (page is null)
            {
                throw new GraphRequestException($"Graph returned an empty assignments body for app '{appId}'.", (int)response.StatusCode, null, null);
            }

            foreach (var dto in page.Value)
            {
                results.Add(AssignmentPayloadMapper.ToCurrentAssignment(dto));
            }

            requestUri = page.NextLink is null ? null : new Uri(page.NextLink);
        }

        return results;
    }

    public async Task CreateAssignmentAsync(string appId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_betaBaseAddress, $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/assignments");
        var payload = AssignmentPayloadMapper.Map(assignment, isWin32);

        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, $"create assignment '{assignment.Key}' on app '{appId}'");
    }

    public async Task UpdateAssignmentAsync(string appId, string assignmentId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_betaBaseAddress, $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/assignments/{Uri.EscapeDataString(assignmentId)}");
        var payload = AssignmentPayloadMapper.Map(assignment, isWin32);

        using var request = new HttpRequestMessage(HttpMethod.Patch, requestUri)
        {
            Content = JsonContent.Create(payload),
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, $"update assignment '{assignment.Key}' on app '{appId}'");
    }

    public async Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_betaBaseAddress, $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/assignments/{Uri.EscapeDataString(assignmentId)}");

        using var response = await _httpClient.DeleteAsync(requestUri, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, $"delete assignment '{assignmentId}' on app '{appId}'");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Error bodies may echo request payloads; only status and correlation ids are surfaced.
        throw new GraphRequestException(
            $"Failed to {operation}: Graph returned {(int)response.StatusCode}.",
            (int)response.StatusCode,
            GetHeader(response, "client-request-id"),
            GetHeader(response, "request-id"));
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
