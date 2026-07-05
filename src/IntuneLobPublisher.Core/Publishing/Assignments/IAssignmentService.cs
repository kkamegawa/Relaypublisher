using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>Computes and applies assignment plans (doc/02-dotnet-architecture.md 9.7).</summary>
public interface IAssignmentService
{
    /// <summary>Reads the app's current assignments and computes the plan for the manifest entry.</summary>
    Task<AssignmentPlan> CreatePlanAsync(
        string mobileAppId,
        AppManifest app,
        AssignmentSyncMode syncMode,
        CancellationToken cancellationToken);

    /// <summary>Applies the plan via Graph: add/update/remove entries in plan order, logging each target.</summary>
    Task ApplyAsync(AssignmentPlan plan, AppManifest app, CancellationToken cancellationToken);
}
