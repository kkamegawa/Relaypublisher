using System.Text;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Renders an <see cref="AssignmentPlan"/> as deterministic text for dry-run output.
/// Only app/group/filter GUIDs and enum values appear — never secrets.
/// </summary>
public static class AssignmentPlanFormatter
{
    public static string Format(AssignmentPlan plan)
    {
        var counts = new Dictionary<AssignmentPlanAction, int>
        {
            [AssignmentPlanAction.Add] = 0,
            [AssignmentPlanAction.Update] = 0,
            [AssignmentPlanAction.Keep] = 0,
            [AssignmentPlanAction.Remove] = 0,
        };
        foreach (var entry in plan.Entries)
        {
            counts[entry.Action]++;
        }

        var builder = new StringBuilder();
        builder.Append($"Assignment plan for app {plan.AppId} (sync: {plan.Mode.ToString().ToLowerInvariant()}): ");
        builder.AppendLine($"{counts[AssignmentPlanAction.Add]} add, {counts[AssignmentPlanAction.Update]} update, {counts[AssignmentPlanAction.Keep]} keep, {counts[AssignmentPlanAction.Remove]} remove");

        foreach (var entry in plan.Entries)
        {
            builder.AppendLine(FormatEntry(entry));
        }

        return builder.ToString();
    }

    private static string FormatEntry(AssignmentPlanEntry entry)
    {
        var symbol = entry.Action switch
        {
            AssignmentPlanAction.Add => "+",
            AssignmentPlanAction.Update => "~",
            AssignmentPlanAction.Remove => "-",
            _ => "=",
        };

        // Update lines show the transition; other lines show the surviving (or removed) state.
        var state = entry.Action == AssignmentPlanAction.Update
            ? string.Join(", ", entry.Changes)
            : DescribeState(entry.Desired, entry.Current);

        return $"  {symbol} {entry.Key}: {state}";
    }

    private static string DescribeState(DesiredAssignment? desired, CurrentAssignment? current)
    {
        var (intent, filter, settings) = desired is not null
            ? (desired.Intent, desired.Filter, desired.Settings)
            : (current!.Intent, current.Filter, current.Settings);

        var parts = new List<string> { $"intent={intent}" };
        if (filter is not null)
        {
            parts.Add($"filter={filter.Mode.ToString().ToLowerInvariant()} {filter.FilterId}");
        }

        if (settings != NormalizedAssignmentSettings.Default)
        {
            parts.Add($"notifications={settings.Notifications}");
            if (settings.RestartGracePeriodMinutes is not null)
            {
                parts.Add($"restartGracePeriodMinutes={settings.RestartGracePeriodMinutes}");
            }
        }

        return string.Join(", ", parts);
    }
}
