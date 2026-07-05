using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>One page of <c>GET /deviceAppManagement/mobileApps/{id}/assignments</c>.</summary>
internal sealed class MobileAppAssignmentListPage
{
    [JsonPropertyName("value")]
    public List<MobileAppAssignmentDto> Value { get; init; } = [];

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>
/// A Graph <c>mobileAppAssignment</c>, used for both reading current assignments and writing
/// create/update payloads. <see cref="Settings"/> stays an opaque <see cref="JsonElement"/> on
/// read because its shape is app-type specific; <see cref="AssignmentPayloadMapper"/> extracts
/// the fields the manifest models.
/// </summary>
internal sealed class MobileAppAssignmentDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.mobileAppAssignment";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("intent")]
    public required string Intent { get; init; }

    [JsonPropertyName("target")]
    public required AssignmentTargetDto Target { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Settings { get; init; }
}

/// <summary>
/// A Graph <c>deviceAndAppManagementAssignmentTarget</c>. The four supported concrete types are
/// distinguished by <see cref="ODataType"/>; only group targets carry <see cref="GroupId"/>.
/// Filter properties exist on the beta endpoint only (doc/issues/issue-004-assignment-merge.md).
/// </summary>
internal sealed class AssignmentTargetDto
{
    public const string GroupType = "#microsoft.graph.groupAssignmentTarget";
    public const string ExclusionGroupType = "#microsoft.graph.exclusionGroupAssignmentTarget";
    public const string AllDevicesType = "#microsoft.graph.allDevicesAssignmentTarget";
    public const string AllLicensedUsersType = "#microsoft.graph.allLicensedUsersAssignmentTarget";

    [JsonPropertyName("@odata.type")]
    public required string ODataType { get; init; }

    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; init; }

    [JsonPropertyName("deviceAndAppManagementAssignmentFilterId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterId { get; init; }

    [JsonPropertyName("deviceAndAppManagementAssignmentFilterType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterType { get; init; }
}

/// <summary>Outbound <c>win32LobAppAssignmentSettings</c> carrying the fields the manifest models.</summary>
internal sealed class Win32LobAppAssignmentSettingsDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppAssignmentSettings";

    [JsonPropertyName("notifications")]
    public required string Notifications { get; init; }

    [JsonPropertyName("restartSettings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Win32LobAppRestartSettingsDto? RestartSettings { get; init; }
}

/// <summary>Outbound <c>win32LobAppRestartSettings</c>.</summary>
internal sealed class Win32LobAppRestartSettingsDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppRestartSettings";

    [JsonPropertyName("gracePeriodInMinutes")]
    public required int GracePeriodInMinutes { get; init; }

    [JsonPropertyName("countdownDisplayBeforeRestartInMinutes")]
    public required int CountdownDisplayBeforeRestartInMinutes { get; init; }
}
