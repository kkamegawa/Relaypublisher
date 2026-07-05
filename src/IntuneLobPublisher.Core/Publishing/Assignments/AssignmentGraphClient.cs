using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

public interface IAssignmentGraphClient
{
    Task<IReadOnlyList<CurrentAssignment>> ListAssignmentsAsync(string appId, CancellationToken cancellationToken);

    Task<string> CreateAssignmentAsync(string appId, DesiredAssignment assignment, CancellationToken cancellationToken);

    Task UpdateAssignmentAsync(string appId, CurrentAssignment current, DesiredAssignment desired, CancellationToken cancellationToken);

    Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken);
}

/// <summary>
/// Applies mobile app assignment CRUD calls through Microsoft Graph. Normal assignments use v1.0;
/// filter-bearing assignments use beta because the assignment filter target fields are beta-only.
/// </summary>
public sealed class AssignmentGraphClient : IAssignmentGraphClient
{
    private readonly HttpClient _httpClient;

    public AssignmentGraphClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<CurrentAssignment>> ListAssignmentsAsync(string appId, CancellationToken cancellationToken)
    {
        var results = new List<CurrentAssignment>();
        string? requestUri = AssignmentCollectionPath(appId, useBeta: true);

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            var page = await ReadJsonAsync<MobileAppAssignmentListPage>(response, requestUri, cancellationToken).ConfigureAwait(false);

            foreach (var assignment in page.Value)
            {
                results.Add(ToCurrentAssignment(assignment));
            }

            requestUri = page.NextLink;
        }

        return results;
    }

    public async Task<string> CreateAssignmentAsync(string appId, DesiredAssignment assignment, CancellationToken cancellationToken)
    {
        var requestUri = AssignmentCollectionPath(appId, useBeta: assignment.Filter is not null);
        var payload = ToPayload(assignment);
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppAssignmentResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw new GraphRequestException($"Graph returned an assignment without an id for '{requestUri}'.", (int)response.StatusCode, null, null);
    }

    public async Task UpdateAssignmentAsync(string appId, CurrentAssignment current, DesiredAssignment desired, CancellationToken cancellationToken)
    {
        var assignmentId = RequireAssignmentId(current);
        var useBeta = current.Filter is not null || desired.Filter is not null;
        var requestUri = AssignmentItemPath(appId, assignmentId, useBeta);
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(ToPayload(desired)), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken)
    {
        var requestUri = AssignmentItemPath(appId, assignmentId, useBeta: false);
        using var response = await _httpClient.DeleteAsync(requestUri, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    private static MobileAppAssignmentPayload ToPayload(DesiredAssignment assignment)
        => new()
        {
            Intent = assignment.Intent,
            Target = ToTargetPayload(assignment.Key, assignment.Filter),
            Settings = assignment.Settings == NormalizedAssignmentSettings.Default ? null : ToSettingsPayload(assignment.Settings),
        };

    private static AssignmentTargetPayload ToTargetPayload(AssignmentTargetKey key, AssignmentFilter? filter)
    {
        var target = key.Kind switch
        {
            AssignmentTargetKind.Group when key.IsExclusion => new AssignmentTargetPayload
            {
                ODataType = "#microsoft.graph.exclusionGroupAssignmentTarget",
                GroupId = key.GroupId?.ToString(),
            },
            AssignmentTargetKind.Group => new AssignmentTargetPayload
            {
                ODataType = "#microsoft.graph.groupAssignmentTarget",
                GroupId = key.GroupId?.ToString(),
            },
            AssignmentTargetKind.AllDevices => new AssignmentTargetPayload
            {
                ODataType = "#microsoft.graph.allDevicesAssignmentTarget",
            },
            AssignmentTargetKind.AllLicensedUsers => new AssignmentTargetPayload
            {
                ODataType = "#microsoft.graph.allLicensedUsersAssignmentTarget",
            },
            _ => throw new AssignmentPlanningException($"Assignment target '{key}' is not supported."),
        };

        if (filter is not null)
        {
            target.DeviceAndAppManagementAssignmentFilterId = filter.FilterId.ToString();
            target.DeviceAndAppManagementAssignmentFilterType = filter.Mode.ToString().ToLowerInvariant();
        }

        return target;
    }

    private static Win32LobAppAssignmentSettingsPayload ToSettingsPayload(NormalizedAssignmentSettings settings)
        => new()
        {
            Notifications = settings.Notifications,
            RestartSettings = settings.RestartGracePeriodMinutes is null
                ? null
                : new Win32LobAppRestartSettingsPayload { GracePeriodInMinutes = settings.RestartGracePeriodMinutes.Value },
        };

    private static CurrentAssignment ToCurrentAssignment(MobileAppAssignmentResponse assignment)
    {
        var target = assignment.Target ?? throw new AssignmentPlanningException($"Graph assignment '{assignment.Id}' has no target.");
        var key = ToTargetKey(assignment.Id, target);
        return new CurrentAssignment(
            assignment.Id,
            key,
            key.IsExclusion ? "required" : assignment.Intent ?? "required",
            ToFilter(assignment.Id, target),
            ToSettings(assignment.Settings));
    }

    private static AssignmentTargetKey ToTargetKey(string? assignmentId, AssignmentTargetPayload target)
    {
        var odataType = NormalizeODataType(target.ODataType);
        return odataType switch
        {
            "microsoft.graph.groupAssignmentTarget" => new AssignmentTargetKey(
                AssignmentTargetKind.Group,
                ParseRequiredGuid(target.GroupId, "groupId", assignmentId),
                IsExclusion: false),
            "microsoft.graph.exclusionGroupAssignmentTarget" => new AssignmentTargetKey(
                AssignmentTargetKind.Group,
                ParseRequiredGuid(target.GroupId, "groupId", assignmentId),
                IsExclusion: true),
            "microsoft.graph.allDevicesAssignmentTarget" => new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, IsExclusion: false),
            "microsoft.graph.allLicensedUsersAssignmentTarget" => new AssignmentTargetKey(AssignmentTargetKind.AllLicensedUsers, null, IsExclusion: false),
            _ => throw new AssignmentPlanningException($"Graph assignment '{assignmentId}' has unsupported target type '{target.ODataType}'."),
        };
    }

    private static AssignmentFilter? ToFilter(string? assignmentId, AssignmentTargetPayload target)
    {
        if (string.IsNullOrWhiteSpace(target.DeviceAndAppManagementAssignmentFilterId)
            || string.Equals(target.DeviceAndAppManagementAssignmentFilterType, "none", StringComparison.Ordinal))
        {
            return null;
        }

        var mode = target.DeviceAndAppManagementAssignmentFilterType switch
        {
            "include" => AssignmentFilterMode.Include,
            "exclude" => AssignmentFilterMode.Exclude,
            _ => throw new AssignmentPlanningException($"Graph assignment filter type '{target.DeviceAndAppManagementAssignmentFilterType}' is not supported."),
        };

        return new AssignmentFilter(ParseRequiredGuid(target.DeviceAndAppManagementAssignmentFilterId, "deviceAndAppManagementAssignmentFilterId", assignmentId), mode);
    }

    private static NormalizedAssignmentSettings ToSettings(Win32LobAppAssignmentSettingsPayload? settings)
        => settings is null
            ? NormalizedAssignmentSettings.Default
            : new NormalizedAssignmentSettings(
                settings.Notifications ?? NormalizedAssignmentSettings.Default.Notifications,
                settings.RestartSettings?.GracePeriodInMinutes);

    private static Guid ParseRequiredGuid(string? value, string fieldName, string? assignmentId)
    {
        if (value is null || !Guid.TryParse(value, out var parsed))
        {
            var assignmentDescription = assignmentId is null
                ? "Graph assignment"
                : $"Graph assignment '{assignmentId}'";
            throw new AssignmentPlanningException($"{assignmentDescription} has invalid {fieldName} '{value}'.");
        }

        return parsed;
    }

    private static string RequireAssignmentId(CurrentAssignment current)
        => current.Id ?? throw new AssignmentPlanningException($"Current assignment for target '{current.Key}' has no Graph id.");

    private static string NormalizeODataType(string? value)
        => value?.TrimStart('#') ?? string.Empty;

    private static string AssignmentCollectionPath(string appId, bool useBeta)
        => $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/assignments".WithGraphVersion(useBeta);

    private static string AssignmentItemPath(string appId, string assignmentId, bool useBeta)
        => $"{AssignmentCollectionPath(appId, useBeta)}/{Uri.EscapeDataString(assignmentId)}";

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
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

    private sealed class MobileAppAssignmentListPage
    {
        [JsonPropertyName("value")]
        public List<MobileAppAssignmentResponse> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }
}

public sealed class MobileAppAssignmentPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.mobileAppAssignment";

    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("target")]
    public required AssignmentTargetPayload Target { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Win32LobAppAssignmentSettingsPayload? Settings { get; init; }
}

public sealed class MobileAppAssignmentResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("target")]
    public AssignmentTargetPayload? Target { get; init; }

    [JsonPropertyName("settings")]
    public Win32LobAppAssignmentSettingsPayload? Settings { get; init; }
}

public sealed class AssignmentTargetPayload
{
    [JsonPropertyName("@odata.type")]
    public string? ODataType { get; set; }

    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }

    [JsonPropertyName("deviceAndAppManagementAssignmentFilterId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceAndAppManagementAssignmentFilterId { get; set; }

    [JsonPropertyName("deviceAndAppManagementAssignmentFilterType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceAndAppManagementAssignmentFilterType { get; set; }
}

public sealed class Win32LobAppAssignmentSettingsPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppAssignmentSettings";

    [JsonPropertyName("notifications")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notifications { get; init; }

    [JsonPropertyName("restartSettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Win32LobAppRestartSettingsPayload? RestartSettings { get; init; }
}

public sealed class Win32LobAppRestartSettingsPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppRestartSettings";

    [JsonPropertyName("gracePeriodInMinutes")]
    public required int GracePeriodInMinutes { get; init; }
}

internal static class AssignmentGraphPathExtensions
{
    public static string WithGraphVersion(this string path, bool useBeta)
        => useBeta ? $"/beta/{path}" : $"/v1.0/{path}";
}
