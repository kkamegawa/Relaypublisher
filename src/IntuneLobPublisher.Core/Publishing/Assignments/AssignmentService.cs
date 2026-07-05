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
                    await _graphClient.CreateAssignmentAsync(plan.AppId, RequireDesired(entry), cancellationToken).ConfigureAwait(false);
                    break;
                case AssignmentPlanAction.Update:
                    await _graphClient.UpdateAssignmentAsync(plan.AppId, RequireCurrent(entry), RequireDesired(entry), cancellationToken).ConfigureAwait(false);
                    break;
                case AssignmentPlanAction.Remove:
                    await _graphClient.DeleteAssignmentAsync(plan.AppId, RequireCurrentId(entry), cancellationToken).ConfigureAwait(false);
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
