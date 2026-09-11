using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Publishing.Categories;

/// <summary>
/// One tenant-wide <c>mobileAppCategory</c>, reduced to the two fields this tool uses. Category
/// resources themselves are never created, renamed or deleted here (doc/00-overview.md §6.20); only
/// the app relationship is managed.
/// </summary>
public sealed record IntuneAppCategory(string Id, string DisplayName);

/// <summary>What the plan does with one category relationship.</summary>
public enum CategoryPlanAction
{
    /// <summary>The manifest asks for the category and the app is not related to it yet: POST the <c>$ref</c>.</summary>
    Add,

    /// <summary>The manifest asks for the category and the relationship already exists: nothing to do.</summary>
    Keep,

    /// <summary>The app is related to a category the manifest does not list: DELETE the <c>$ref</c>.</summary>
    Remove,
}

/// <summary>One line of the category plan.</summary>
/// <param name="CategoryId">Tenant category id. Always known: adds resolve the id before any write, removes read it from the app.</param>
/// <param name="DisplayName">Category display name as returned by Graph, not the manifest spelling.</param>
public sealed record CategoryPlanEntry(
    CategoryPlanAction Action,
    string CategoryId,
    string DisplayName);

/// <summary>
/// The computed category plan for one Intune app. <c>Requested</c> distinguishes "the manifest
/// omitted <c>Categories</c>" (no category Graph read, no write, existing relationships preserved)
/// from "the manifest asked for the empty set" (every relationship is removed).
/// </summary>
/// <param name="AppId">Real app id, or <see cref="PublishOrchestrator.NewAppPlaceholderId"/> while the app does not exist yet.</param>
public sealed record CategoryPlan(
    string AppId,
    bool Requested,
    IReadOnlyList<CategoryPlanEntry> Entries)
{
    /// <summary>Plan for an app entry whose manifest does not declare <c>Categories</c>.</summary>
    public static CategoryPlan NotRequested(string appId) => new(appId, Requested: false, []);

    /// <summary>True when applying the plan would call Graph (any add or remove).</summary>
    public bool HasChanges => Entries.Any(e => e.Action != CategoryPlanAction.Keep);
}

/// <summary>
/// Picks the Graph API version for category calls the same way the app and content clients do
/// (doc/00-overview.md §6.13): Windows (<c>win32LobApp</c>) and macOS (<c>macOSPkgApp</c> /
/// <c>macOSLobApp</c>, both <c>AppType</c> values) all stay on beta - Windows because
/// <c>displayVersion</c>/<c>roleScopeTagIds</c> are beta-only, macOS because <c>macOSPkgApp</c>
/// doesn't exist in v1.0 and <c>macOSLobApp</c> shares the same <c>roleScopeTagIds</c> gap
/// (doc/adr/publishing.md 2026-09-10 entry). Category relationships exist on <c>mobileApp</c> in
/// both API versions, so this only has to stay consistent with the version the app/content calls
/// for that platform use - which, for every platform this tool supports, is now always beta. Filter-less
/// <see cref="Assignments.AssignmentGraphClient"/> calls are a separate, still-v1.0 exception
/// unrelated to category routing; unifying them too is Issue #164's job.
/// </summary>
public static class CategoryApiVersion
{
    public static bool UseBeta(AppManifest app) => true;
}
