using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>
/// Orchestrates assignment sync for one app: current state via <see cref="IGraphAppAssignmentClient"/>,
/// plan via <see cref="AssignmentPlanner"/>, then one Graph call per add/update/remove entry so a
/// merge never touches assignments the manifest does not mention.
/// </summary>
public sealed class AssignmentService : IAssignmentService
{
    private readonly IGraphAppAssignmentClient _client;
    private readonly ILogger<AssignmentService> _logger;

    public AssignmentService(IGraphAppAssignmentClient client, ILogger<AssignmentService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AssignmentPlan> CreatePlanAsync(
        string mobileAppId,
        AppManifest app,
        AssignmentSyncMode syncMode,
        CancellationToken cancellationToken)
    {
        var current = await _client.GetAssignmentsAsync(mobileAppId, cancellationToken).ConfigureAwait(false);
        return AssignmentPlanner.CreatePlan(mobileAppId, app, syncMode, current);
    }

    public async Task ApplyAsync(AssignmentPlan plan, AppManifest app, CancellationToken cancellationToken)
    {
        GuardMacOsPkgUninstall(plan, app);

        if (!plan.HasChanges)
        {
            _logger.LogInformation("Assignments for app {AppId} already match the manifest; nothing to apply.", plan.AppId);
            return;
        }

        var isWin32 = app.Platform == "windows" && app.InstallerType == "win32";

        foreach (var entry in plan.Entries)
        {
            switch (entry.Action)
            {
                case AssignmentPlanAction.Add:
                    _logger.LogInformation("Assignment add for app {AppId}: {Target} intent={Intent}", plan.AppId, entry.Key, entry.Desired!.Intent);
                    await _client.CreateAssignmentAsync(plan.AppId, entry.Desired!, isWin32, cancellationToken).ConfigureAwait(false);
                    break;

                case AssignmentPlanAction.Update:
                    _logger.LogInformation("Assignment update for app {AppId}: {Target} ({Changes})", plan.AppId, entry.Key, string.Join(", ", entry.Changes));
                    await _client.UpdateAssignmentAsync(plan.AppId, RequireId(entry), entry.Desired!, isWin32, cancellationToken).ConfigureAwait(false);
                    break;

                case AssignmentPlanAction.Remove:
                    _logger.LogInformation("Assignment remove for app {AppId}: {Target}", plan.AppId, entry.Key);
                    await _client.DeleteAssignmentAsync(plan.AppId, RequireId(entry), cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    // Keep entries are never sent to Graph; that is the core merge guarantee.
                    break;
            }
        }

        var applied = plan.Entries.Count(e => e.Action != AssignmentPlanAction.Keep);
        _logger.LogInformation("Applied {Count} assignment change(s) to app {AppId}.", applied, plan.AppId);
    }

    private static string RequireId(AssignmentPlanEntry entry)
        => entry.Current?.Id
            ?? throw new AssignmentPlanningException($"Plan entry '{entry.Key}' has no assignment id to {entry.Action.ToString().ToLowerInvariant()}.");

    private static void GuardMacOsPkgUninstall(AssignmentPlan plan, AppManifest app)
    {
        // The planner already rejects this; keep a cheap guard in front of the writes.
        var isMacOsPkg = app.Platform == "macos" && (app.AppType ?? "pkg") == "pkg";
        if (isMacOsPkg && plan.Entries.Any(e => e.Desired?.Intent == "uninstall"))
        {
            throw new AssignmentPlanningException("Intent 'uninstall' is not supported for macOS AppType 'pkg' apps.");
        }
    }
}
