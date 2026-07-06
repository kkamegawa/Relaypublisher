using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

public interface IAssignmentService
{
    Task<AssignmentPlan> CreatePlanAsync(
        string appId,
        AppManifest app,
        AssignmentSyncMode mode,
        CancellationToken cancellationToken);

    Task ApplyAsync(
        AssignmentPlan plan,
        AppManifest app,
        CancellationToken cancellationToken);
}

/// <summary>Fetches current assignments, computes a plan, and applies non-keep entries through Graph.</summary>
public sealed class AssignmentService : IAssignmentService
{
    private readonly IAssignmentGraphClient _graphClient;
    private readonly ILogger<AssignmentService> _logger;

    public AssignmentService(IAssignmentGraphClient graphClient, ILogger<AssignmentService> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
    }

    public async Task<AssignmentPlan> CreatePlanAsync(
        string appId,
        AppManifest app,
        AssignmentSyncMode mode,
        CancellationToken cancellationToken)
    {
        var current = await _graphClient.ListAssignmentsAsync(appId, cancellationToken).ConfigureAwait(false);
        return AssignmentPlanner.CreatePlan(appId, app, mode, current);
    }

    public async Task ApplyAsync(
        AssignmentPlan plan,
        AppManifest app,
        CancellationToken cancellationToken)
    {
        GuardMacOsPkgUninstall(app);

        foreach (var entry in plan.Entries)
        {
            LogPlanEntry(plan.AppId, entry);
            switch (entry.Action)
            {
                case AssignmentPlanAction.Add:
                    var createdId = await _graphClient.CreateAssignmentAsync(plan.AppId, RequireDesired(entry), cancellationToken).ConfigureAwait(false);
                    LogApplied(plan.AppId, entry, createdId);
                    break;
                case AssignmentPlanAction.Update:
                    await _graphClient.UpdateAssignmentAsync(plan.AppId, RequireCurrent(entry), RequireDesired(entry), cancellationToken).ConfigureAwait(false);
                    LogApplied(plan.AppId, entry, RequireCurrentId(entry));
                    break;
                case AssignmentPlanAction.Remove:
                    var removedId = RequireCurrentId(entry);
                    await _graphClient.DeleteAssignmentAsync(plan.AppId, removedId, cancellationToken).ConfigureAwait(false);
                    LogApplied(plan.AppId, entry, removedId);
                    break;
                case AssignmentPlanAction.Keep:
                    break;
                default:
                    throw new AssignmentPlanningException($"Assignment plan action '{entry.Action}' is not supported.");
            }
        }
    }

    private static void GuardMacOsPkgUninstall(AppManifest app)
    {
        var isMacOsPkg = app.Platform == "macos" && (app.AppType ?? "pkg") == "pkg";
        if (isMacOsPkg && app.Assignments.Any(a => a.Intent == "uninstall"))
        {
            throw new AssignmentPlanningException("Intent 'uninstall' is not supported for macOS AppType 'pkg' apps.");
        }
    }

    private static DesiredAssignment RequireDesired(AssignmentPlanEntry entry)
        => entry.Desired ?? throw new AssignmentPlanningException($"Assignment plan entry '{entry.Action}' for target '{entry.Key}' has no desired assignment.");

    private static CurrentAssignment RequireCurrent(AssignmentPlanEntry entry)
        => entry.Current ?? throw new AssignmentPlanningException($"Assignment plan entry '{entry.Action}' for target '{entry.Key}' has no current assignment.");

    private static string RequireCurrentId(AssignmentPlanEntry entry)
        => RequireCurrent(entry).Id ?? throw new AssignmentPlanningException($"Assignment plan entry '{entry.Action}' for target '{entry.Key}' has no current assignment id.");

    /// <summary>Logs the outcome of a successful Graph write, keyed by target (group id or built-in target).</summary>
    private void LogApplied(string appId, AssignmentPlanEntry entry, string assignmentId)
    {
        _logger.LogInformation(
            "Assignment {Action} applied for app {AppId}: target={Target} assignmentId={AssignmentId}",
            entry.Action.ToString().ToLowerInvariant(),
            appId,
            entry.Key,
            assignmentId);
    }

    private void LogPlanEntry(string appId, AssignmentPlanEntry entry)
    {
        var desired = entry.Desired;
        var current = entry.Current;
        var assignmentId = current?.Id ?? "(new)";
        var intent = desired?.Intent ?? current?.Intent ?? "(none)";
        var filter = desired?.Filter ?? current?.Filter;

        _logger.LogInformation(
            "Assignment {Action} for app {AppId}: target={Target} intent={Intent} filter={Filter} assignmentId={AssignmentId}",
            entry.Action.ToString().ToLowerInvariant(),
            appId,
            entry.Key,
            intent,
            filter is null ? "none" : $"{filter.Mode.ToString().ToLowerInvariant()} {filter.FilterId}",
            assignmentId);
    }
}
