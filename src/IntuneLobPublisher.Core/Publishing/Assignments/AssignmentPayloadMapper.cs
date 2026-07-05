using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Maps between canonical assignment records and Graph <c>mobileAppAssignment</c> DTOs.
/// Pure logic — no I/O.
/// </summary>
internal static class AssignmentPayloadMapper
{
    /// <summary>Builds the create/update payload for one desired assignment.</summary>
    /// <param name="isWin32">
    /// True for win32 apps, which are the only app type whose assignment settings the manifest
    /// models. Settings are sent for include targets only; Graph rejects settings on exclusions.
    /// </param>
    public static MobileAppAssignmentDto Map(DesiredAssignment assignment, bool isWin32)
    {
        return new MobileAppAssignmentDto
        {
            Intent = assignment.Intent,
            Target = MapTarget(assignment.Key, assignment.Filter),
            Settings = isWin32 && !assignment.Key.IsExclusion ? SerializeSettings(assignment.Settings) : null,
        };
    }

    /// <summary>Converts a Graph assignment into the canonical form the planner compares against.</summary>
    public static CurrentAssignment ToCurrentAssignment(MobileAppAssignmentDto dto)
    {
        var key = ToTargetKey(dto.Target);

        // Exclusions are pinned to "required" on both sides; see AssignmentNormalizer.
        var intent = key.IsExclusion ? "required" : dto.Intent;

        return new CurrentAssignment(dto.Id, key, intent, ToFilter(dto.Target), ToSettings(dto.Settings));
    }

    private static AssignmentTargetDto MapTarget(AssignmentTargetKey key, AssignmentFilter? filter)
    {
        var odataType = key.Kind switch
        {
            AssignmentTargetKind.Group when key.IsExclusion => AssignmentTargetDto.ExclusionGroupType,
            AssignmentTargetKind.Group => AssignmentTargetDto.GroupType,
            AssignmentTargetKind.AllDevices => AssignmentTargetDto.AllDevicesType,
            _ => AssignmentTargetDto.AllLicensedUsersType,
        };

        return new AssignmentTargetDto
        {
            ODataType = odataType,
            GroupId = key.GroupId?.ToString("D"),
            FilterId = filter?.FilterId.ToString("D"),
            FilterType = filter?.Mode switch
            {
                AssignmentFilterMode.Include => "include",
                AssignmentFilterMode.Exclude => "exclude",
                _ => null,
            },
        };
    }

    private static AssignmentTargetKey ToTargetKey(AssignmentTargetDto target)
    {
        // Graph responses may omit the leading '#'.
        var odataType = target.ODataType.StartsWith('#') ? target.ODataType : "#" + target.ODataType;

        return odataType switch
        {
            AssignmentTargetDto.GroupType => new AssignmentTargetKey(AssignmentTargetKind.Group, ParseGroupId(target), IsExclusion: false),
            AssignmentTargetDto.ExclusionGroupType => new AssignmentTargetKey(AssignmentTargetKind.Group, ParseGroupId(target), IsExclusion: true),
            AssignmentTargetDto.AllDevicesType => new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, IsExclusion: false),
            AssignmentTargetDto.AllLicensedUsersType => new AssignmentTargetKey(AssignmentTargetKind.AllLicensedUsers, null, IsExclusion: false),
            // For example configurationManagerCollectionAssignmentTarget: refuse to plan rather
            // than silently mishandling an assignment this tool cannot represent.
            _ => throw new AssignmentPlanningException($"Assignment target type '{target.ODataType}' is not supported by this tool."),
        };
    }

    private static Guid ParseGroupId(AssignmentTargetDto target)
    {
        if (!Guid.TryParse(target.GroupId, out var groupId))
        {
            throw new AssignmentPlanningException($"Graph returned a group assignment target with an invalid groupId '{target.GroupId}'.");
        }

        return groupId;
    }

    private static AssignmentFilter? ToFilter(AssignmentTargetDto target)
    {
        // filterType "none" (with the empty-GUID filter id) means no filter.
        if (target.FilterId is null || target.FilterType is not ("include" or "exclude"))
        {
            return null;
        }

        if (!Guid.TryParse(target.FilterId, out var filterId) || filterId == Guid.Empty)
        {
            return null;
        }

        var mode = target.FilterType == "include" ? AssignmentFilterMode.Include : AssignmentFilterMode.Exclude;
        return new AssignmentFilter(filterId, mode);
    }

    private static NormalizedAssignmentSettings ToSettings(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } element)
        {
            return NormalizedAssignmentSettings.Default;
        }

        var notifications = element.TryGetProperty("notifications", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : NormalizedAssignmentSettings.Default.Notifications;

        int? gracePeriod = null;
        if (element.TryGetProperty("restartSettings", out var restart)
            && restart.ValueKind == JsonValueKind.Object
            && restart.TryGetProperty("gracePeriodInMinutes", out var grace)
            && grace.ValueKind == JsonValueKind.Number)
        {
            gracePeriod = grace.GetInt32();
        }

        return new NormalizedAssignmentSettings(notifications, gracePeriod);
    }

    private static JsonElement SerializeSettings(NormalizedAssignmentSettings settings)
    {
        var dto = new Win32LobAppAssignmentSettingsDto
        {
            Notifications = settings.Notifications,
            RestartSettings = settings.RestartGracePeriodMinutes is int grace
                ? new Win32LobAppRestartSettingsDto
                {
                    GracePeriodInMinutes = grace,
                    // Intune requires the countdown to fit inside the grace period; 15 minutes
                    // matches the portal default.
                    CountdownDisplayBeforeRestartInMinutes = Math.Min(15, grace),
                }
                : null,
        };

        return JsonSerializer.SerializeToElement(dto);
    }
}
