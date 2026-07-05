using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>How manifest assignments are synchronized with the app's current Intune assignments (doc/01-manifest-schema.md 5.6).</summary>
public enum AssignmentSyncMode
{
    /// <summary>Per-target upsert. Existing assignments not listed in the manifest are never removed.</summary>
    Merge,

    /// <summary>The manifest assignment list is authoritative; existing assignments not in the manifest are removed.</summary>
    Replace,
}

/// <summary>Parses the manifest `AssignmentSync` value. The manifest validator restricts it to merge/replace already.</summary>
public static class AssignmentSyncModes
{
    public static AssignmentSyncMode Parse(string? value) => value switch
    {
        null or "merge" => AssignmentSyncMode.Merge,
        "replace" => AssignmentSyncMode.Replace,
        _ => throw new AssignmentPlanningException($"AssignmentSync '{value}' is not supported. Allowed values: merge, replace."),
    };
}

/// <summary>Which Graph assignment target type an assignment addresses.</summary>
public enum AssignmentTargetKind
{
    /// <summary>groupAssignmentTarget / exclusionGroupAssignmentTarget.</summary>
    Group,

    /// <summary>allDevicesAssignmentTarget.</summary>
    AllDevices,

    /// <summary>allLicensedUsersAssignmentTarget.</summary>
    AllLicensedUsers,
}

/// <summary>
/// Identity of an assignment target used as the merge key: two assignments address the same target
/// exactly when their keys are equal. GroupId is a parsed <see cref="Guid"/> so casing differences
/// between manifest and Graph never produce a spurious add/remove pair.
/// </summary>
public readonly record struct AssignmentTargetKey(AssignmentTargetKind Kind, Guid? GroupId, bool IsExclusion)
{
    public override string ToString() => Kind switch
    {
        AssignmentTargetKind.Group when IsExclusion => $"group {GroupId} (exclude)",
        AssignmentTargetKind.Group => $"group {GroupId}",
        AssignmentTargetKind.AllDevices => "allDevices",
        _ => "allLicensedUsers",
    };
}

/// <summary>Assignment filter mode (`deviceAndAppManagementAssignmentFilterType`).</summary>
public enum AssignmentFilterMode
{
    Include,
    Exclude,
}

/// <summary>An assignment filter reference. Absent (null) means no filter, which also covers Graph's `none` filter type.</summary>
public sealed record AssignmentFilter(Guid FilterId, AssignmentFilterMode Mode);

/// <summary>
/// Assignment settings normalized so "manifest omitted Settings" and "Graph default settings"
/// compare as equal instead of producing a noisy update. Only fields the manifest models are
/// compared; other Graph-side settings are out of scope for drift detection.
/// </summary>
public sealed record NormalizedAssignmentSettings(string Notifications, int? RestartGracePeriodMinutes)
{
    public static readonly NormalizedAssignmentSettings Default = new("showAll", null);
}

/// <summary>An assignment the manifest wants to exist, in canonical form.</summary>
/// <remarks>Exclusion targets carry the normalized intent "required" (Graph requires an intent but ignores it for exclusions).</remarks>
public sealed record DesiredAssignment(
    AssignmentTargetKey Key,
    string Intent,
    AssignmentFilter? Filter,
    NormalizedAssignmentSettings Settings);

/// <summary>An assignment that currently exists on the Intune app, in the same canonical form as <see cref="DesiredAssignment"/>.</summary>
public sealed record CurrentAssignment(
    string? Id,
    AssignmentTargetKey Key,
    string Intent,
    AssignmentFilter? Filter,
    NormalizedAssignmentSettings Settings);

/// <summary>What the plan does with one target.</summary>
public enum AssignmentPlanAction
{
    /// <summary>The target is in the manifest but not on the app: create it.</summary>
    Add,

    /// <summary>The target exists but intent, filter or settings differ: update it to the manifest values.</summary>
    Update,

    /// <summary>The target exists and already matches the manifest, or (merge) is unlisted and preserved.</summary>
    Keep,

    /// <summary>Replace mode only: the target exists but is not in the manifest, so it is removed.</summary>
    Remove,
}

/// <summary>One line of the assignment plan.</summary>
/// <param name="Changes">Human readable differences for <see cref="AssignmentPlanAction.Update"/> entries, empty otherwise.</param>
public sealed record AssignmentPlanEntry(
    AssignmentPlanAction Action,
    AssignmentTargetKey Key,
    DesiredAssignment? Desired,
    CurrentAssignment? Current,
    IReadOnlyList<string> Changes);

/// <summary>The computed assignment plan for one Intune app.</summary>
public sealed record AssignmentPlan(
    string AppId,
    AssignmentSyncMode Mode,
    IReadOnlyList<AssignmentPlanEntry> Entries)
{
    /// <summary>True when applying the plan would call Graph (any non-keep entry).</summary>
    public bool HasChanges => Entries.Any(e => e.Action != AssignmentPlanAction.Keep);
}
