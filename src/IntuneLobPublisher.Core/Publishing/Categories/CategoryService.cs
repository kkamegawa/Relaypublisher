using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing.Categories;

public interface ICategoryService
{
    /// <summary>
    /// Resolves the manifest's category names against the tenant catalog and diffs them against the
    /// app's current relationships. Makes no Graph call at all when <c>Categories</c> is omitted.
    /// </summary>
    /// <param name="existingAppId">The resolved Intune app id, or null when the app does not exist yet.</param>
    Task<CategoryPlan> CreatePlanAsync(string? existingAppId, AppManifest app, CancellationToken cancellationToken);

    /// <summary>Applies the non-keep entries of a plan. A not-requested plan writes nothing.</summary>
    Task ApplyAsync(CategoryPlan plan, AppManifest app, CancellationToken cancellationToken);
}

/// <summary>
/// Synchronizes one app's Intune category relationships: tenant-name preflight, deterministic diff,
/// then <c>$ref</c> add/remove. Name resolution and the current-relationship read both happen before
/// the first category write for that app, so an unusable manifest never leaves the app half
/// synchronized. Failures are surfaced as <see cref="CategorySyncException"/> so one manifest entry's
/// category problem fails only that entry and the CI batch continues; an identity-wide 401/403 on the
/// tenant catalog listing keeps its <see cref="GraphAccessDeniedException"/> and aborts the batch.
/// </summary>
public sealed class CategoryService : ICategoryService
{
    private readonly ICategoryGraphClient _graphClient;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(ICategoryGraphClient graphClient, ILogger<CategoryService> logger)
    {
        _graphClient = graphClient;
        _logger = logger;
    }

    public async Task<CategoryPlan> CreatePlanAsync(string? existingAppId, AppManifest app, CancellationToken cancellationToken)
    {
        var appId = existingAppId ?? PublishOrchestrator.NewAppPlaceholderId;
        if (app.Categories is null)
        {
            // Omitted Categories preserves whatever the app is related to today, and must not cost a
            // single Graph read.
            return CategoryPlan.NotRequested(appId);
        }

        var useBeta = CategoryApiVersion.UseBeta(app);
        var tenantCategories = await GuardAsync(
            () => _graphClient.ListTenantCategoriesAsync(useBeta, cancellationToken),
            "Failed to read the tenant app category catalog.").ConfigureAwait(false);
        var desired = CategoryNameResolver.Resolve(app.Categories, tenantCategories);

        // A brand new app has no relationships yet, and its id does not exist to read them from.
        var current = existingAppId is null
            ? []
            : await GuardAsync(
                () => _graphClient.ListAppCategoriesAsync(existingAppId, useBeta, cancellationToken),
                $"Failed to read the current categories of Intune app '{existingAppId}'.").ConfigureAwait(false);

        return CategoryPlanner.CreatePlan(appId, desired, current);
    }

    public async Task ApplyAsync(CategoryPlan plan, AppManifest app, CancellationToken cancellationToken)
    {
        if (!plan.Requested)
        {
            return;
        }

        if (plan.AppId == PublishOrchestrator.NewAppPlaceholderId)
        {
            throw new CategorySyncException(
                "Cannot apply a category plan before the Intune app has been created.");
        }

        var useBeta = CategoryApiVersion.UseBeta(app);

        // Adds first, removes second: the app keeps at least its intended categories throughout, and
        // the ordering is stable for the console/log output.
        foreach (var entry in plan.Entries.Where(e => e.Action == CategoryPlanAction.Add))
        {
            var added = await GuardAsync(
                () => _graphClient.AddCategoryAsync(plan.AppId, entry.CategoryId, useBeta, cancellationToken),
                $"Category sync failed for '{entry.DisplayName}'.").ConfigureAwait(false);
            LogApplied(plan.AppId, CategoryPlanAction.Add, entry, changed: added);
        }

        foreach (var entry in plan.Entries.Where(e => e.Action == CategoryPlanAction.Remove))
        {
            var removed = await GuardAsync(
                () => _graphClient.RemoveCategoryAsync(plan.AppId, entry.CategoryId, useBeta, cancellationToken),
                $"Category sync failed for '{entry.DisplayName}'.").ConfigureAwait(false);
            LogApplied(plan.AppId, CategoryPlanAction.Remove, entry, changed: removed);
        }
    }

    /// <summary>
    /// Re-labels a per-call Graph failure as a category failure. <see cref="GraphAccessDeniedException"/>
    /// is deliberately not caught: it is identity-wide and must keep aborting the batch.
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation, string message)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (GraphRequestException ex)
        {
            throw new CategorySyncException($"{message} {ex.Message}", ex);
        }
    }

    private void LogApplied(string appId, CategoryPlanAction action, CategoryPlanEntry entry, bool changed)
        => _logger.LogInformation(
            "Category {Action} for app {AppId}: category={DisplayName} categoryId={CategoryId} changed={Changed}",
            action.ToString().ToLowerInvariant(),
            appId,
            entry.DisplayName,
            entry.CategoryId,
            changed);
}
