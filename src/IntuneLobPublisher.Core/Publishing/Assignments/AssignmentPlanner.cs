using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Computes the assignment plan for one app: which targets to add, update, keep or remove
/// (doc/issues/issue-004-assignment-merge.md). Pure logic — no Graph calls, no logging.
/// </summary>
public static class AssignmentPlanner
{
    public static AssignmentPlan CreatePlan(
        string appId,
        AppManifest app,
        AssignmentSyncMode mode,
        IReadOnlyList<CurrentAssignment> currentAssignments)
    {
        GuardMacOsPkgUninstall(app);

        var desired = AssignmentNormalizer.Normalize(app);

        var desiredKeys = new HashSet<AssignmentTargetKey>();
        foreach (var entry in desired)
        {
            if (!desiredKeys.Add(entry.Key))
            {
                // The manifest validator rejects duplicate targets; failing here guards direct callers.
                throw new AssignmentPlanningException($"Manifest contains duplicate assignments for target '{entry.Key}'.");
            }
        }

        var currentByKey = new Dictionary<AssignmentTargetKey, CurrentAssignment>();
        foreach (var current in currentAssignments)
        {
            if (!currentByKey.TryAdd(current.Key, current))
            {
                // Intune cannot hold two assignments for the same target; refuse to plan against inconsistent data.
                throw new AssignmentPlanningException($"Intune app '{appId}' has duplicate assignments for target '{current.Key}'.");
            }
        }

        var entries = new List<AssignmentPlanEntry>();
        var consumedKeys = new HashSet<AssignmentTargetKey>();

        // Manifest entries first, in manifest order, so the diff reads like the manifest.
        foreach (var want in desired)
        {
            consumedKeys.Add(want.Key);
            if (!currentByKey.TryGetValue(want.Key, out var have))
            {
                entries.Add(new AssignmentPlanEntry(AssignmentPlanAction.Add, want.Key, want, null, []));
                continue;
            }

            var changes = Compare(have, want);
            entries.Add(changes.Count == 0
                ? new AssignmentPlanEntry(AssignmentPlanAction.Keep, want.Key, want, have, [])
                : new AssignmentPlanEntry(AssignmentPlanAction.Update, want.Key, want, have, changes));
        }

        // Existing assignments the manifest does not mention: preserved by merge, removed by replace.
        foreach (var have in currentAssignments)
        {
            if (consumedKeys.Contains(have.Key))
            {
                continue;
            }

            entries.Add(mode == AssignmentSyncMode.Merge
                ? new AssignmentPlanEntry(AssignmentPlanAction.Keep, have.Key, null, have, [])
                : new AssignmentPlanEntry(AssignmentPlanAction.Remove, have.Key, null, have, []));
        }

        return new AssignmentPlan(appId, mode, entries);
    }

    private static void GuardMacOsPkgUninstall(AppManifest app)
    {
        var isMacOsPkg = app.Platform == "macos" && (app.AppType ?? "pkg") == "pkg";
        if (isMacOsPkg && app.Assignments.Any(a => a.Intent == "uninstall"))
        {
            throw new AssignmentPlanningException("Intent 'uninstall' is not supported for macOS AppType 'pkg' apps.");
        }
    }

    private static List<string> Compare(CurrentAssignment have, DesiredAssignment want)
    {
        var changes = new List<string>();

        if (!string.Equals(have.Intent, want.Intent, StringComparison.Ordinal))
        {
            changes.Add($"intent: {have.Intent} -> {want.Intent}");
        }

        if (have.Filter != want.Filter)
        {
            changes.Add($"filter: {Describe(have.Filter)} -> {Describe(want.Filter)}");
        }

        if (!string.Equals(have.Settings.Notifications, want.Settings.Notifications, StringComparison.Ordinal))
        {
            changes.Add($"settings.notifications: {have.Settings.Notifications} -> {want.Settings.Notifications}");
        }

        if (have.Settings.RestartGracePeriodMinutes != want.Settings.RestartGracePeriodMinutes)
        {
            changes.Add($"settings.restartGracePeriodMinutes: {Describe(have.Settings.RestartGracePeriodMinutes)} -> {Describe(want.Settings.RestartGracePeriodMinutes)}");
        }

        return changes;
    }

    private static string Describe(AssignmentFilter? filter)
        => filter is null ? "none" : $"{filter.Mode.ToString().ToLowerInvariant()} {filter.FilterId}";

    private static string Describe(int? minutes)
        => minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}
