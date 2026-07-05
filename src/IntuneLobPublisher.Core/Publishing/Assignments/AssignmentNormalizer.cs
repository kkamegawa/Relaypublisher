using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Converts manifest assignment entries into canonical <see cref="DesiredAssignment"/> records.
/// Inputs are expected to have passed <see cref="Validation.ManifestValidator"/>; malformed values
/// still throw <see cref="AssignmentPlanningException"/> as defense in depth rather than producing
/// a wrong plan.
/// </summary>
public static class AssignmentNormalizer
{
    public static IReadOnlyList<DesiredAssignment> Normalize(AppManifest app)
    {
        var desired = new List<DesiredAssignment>(app.Assignments.Count);
        foreach (var assignment in app.Assignments)
        {
            desired.Add(Normalize(assignment));
        }

        return desired;
    }

    private static DesiredAssignment Normalize(AssignmentManifest assignment)
    {
        var target = assignment.Target ?? "group";
        var isExclusion = (assignment.Mode ?? "include") == "exclude";

        var key = target switch
        {
            "group" => new AssignmentTargetKey(AssignmentTargetKind.Group, ParseGuid(assignment.GroupId, "GroupId"), isExclusion),
            "allDevices" => new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, isExclusion),
            "allLicensedUsers" => new AssignmentTargetKey(AssignmentTargetKind.AllLicensedUsers, null, isExclusion),
            _ => throw new AssignmentPlanningException($"Assignment Target '{target}' is not supported."),
        };

        if (key.Kind != AssignmentTargetKind.Group && isExclusion)
        {
            // exclusionGroupAssignmentTarget is the only exclusion target type Graph offers.
            throw new AssignmentPlanningException($"Assignment Mode 'exclude' is only supported for Target 'group', not '{target}'.");
        }

        // Graph requires an intent on every assignment but ignores it for exclusion targets, so
        // exclusions are pinned to "required" on both sides to keep them out of the diff.
        var intent = isExclusion
            ? "required"
            : assignment.Intent ?? throw new AssignmentPlanningException($"Assignment for target '{key}' has no Intent.");

        return new DesiredAssignment(key, intent, NormalizeFilter(assignment), NormalizeSettings(assignment.Settings));
    }

    private static AssignmentFilter? NormalizeFilter(AssignmentManifest assignment)
    {
        if (assignment.FilterId is null)
        {
            return null;
        }

        var mode = assignment.FilterMode switch
        {
            "include" => AssignmentFilterMode.Include,
            "exclude" => AssignmentFilterMode.Exclude,
            _ => throw new AssignmentPlanningException($"FilterMode '{assignment.FilterMode}' is not supported. Allowed values: include, exclude."),
        };

        return new AssignmentFilter(ParseGuid(assignment.FilterId, "FilterId")!.Value, mode);
    }

    private static NormalizedAssignmentSettings NormalizeSettings(AssignmentSettingsManifest? settings)
    {
        if (settings is null)
        {
            return NormalizedAssignmentSettings.Default;
        }

        return new NormalizedAssignmentSettings(
            settings.Notifications ?? NormalizedAssignmentSettings.Default.Notifications,
            settings.RestartGracePeriodMinutes);
    }

    private static Guid? ParseGuid(string? value, string fieldName)
    {
        if (value is null)
        {
            throw new AssignmentPlanningException($"{fieldName} is required but missing.");
        }

        if (!Guid.TryParse(value, out var parsed))
        {
            throw new AssignmentPlanningException($"{fieldName} '{value}' is not a valid GUID.");
        }

        return parsed;
    }
}
