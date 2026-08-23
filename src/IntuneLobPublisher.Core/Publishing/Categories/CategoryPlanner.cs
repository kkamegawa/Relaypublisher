using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Categories;

/// <summary>
/// Maps the category display names a manifest asks for onto tenant category ids using
/// <see cref="StringComparer.OrdinalIgnoreCase"/> exact matching. Pure logic - no Graph calls - so the
/// failure rules are testable on their own: a name with no match or more than one match fails here,
/// which is before the first category write for that app.
/// </summary>
public static class CategoryNameResolver
{
    /// <summary>
    /// Resolves every requested name against <paramref name="tenantCategories"/>, preserving manifest
    /// order. Names are compared verbatim (no trimming, no Unicode normalization); validation already
    /// rejected padded or empty names.
    /// </summary>
    /// <exception cref="CategorySyncException">A requested name matched zero or several tenant categories.</exception>
    public static IReadOnlyList<IntuneAppCategory> Resolve(
        IReadOnlyList<string> requestedNames,
        IReadOnlyList<IntuneAppCategory> tenantCategories)
    {
        var resolved = new List<IntuneAppCategory>(requestedNames.Count);
        foreach (var name in requestedNames)
        {
            var matches = tenantCategories
                .Where(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            resolved.Add(matches.Count switch
            {
                1 => matches[0],
                0 => throw new CategorySyncException(
                    $"Category '{name}' does not exist in the tenant. Create it in Intune first; " +
                    "Relaypublisher never creates, renames or deletes tenant categories."),
                _ => throw new CategorySyncException(
                    $"Category '{name}' matches {matches.Count} tenant categories " +
                    $"({string.Join(", ", matches.Select(m => $"'{m.DisplayName}' ({m.Id})"))}). " +
                    "Category names must be unique up to case for the manifest to address one unambiguously."),
            });
        }

        return resolved;
    }
}

/// <summary>
/// Computes the add/keep/remove plan for one app's category relationships. Pure logic - no Graph
/// calls, no logging. The desired set is exact: anything the app is related to but the manifest does
/// not list is removed, which is what makes <c>Categories: []</c> clear every relationship.
/// </summary>
public static class CategoryPlanner
{
    public static CategoryPlan CreatePlan(
        string appId,
        IReadOnlyList<IntuneAppCategory> desired,
        IReadOnlyList<IntuneAppCategory> current)
    {
        var currentIds = new HashSet<string>(current.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<CategoryPlanEntry>();

        // Manifest order first, so the plan reads like the manifest.
        foreach (var category in desired)
        {
            if (!desiredIds.Add(category.Id))
            {
                // Two manifest names resolved to the same tenant category; keep one line.
                continue;
            }

            entries.Add(new CategoryPlanEntry(
                currentIds.Contains(category.Id) ? CategoryPlanAction.Keep : CategoryPlanAction.Add,
                category.Id,
                category.DisplayName));
        }

        // Relationships the manifest does not list. Sorted so the plan text does not depend on the
        // order Graph happened to return the app's categories in.
        foreach (var category in current
                     .Where(c => !desiredIds.Contains(c.Id))
                     .OrderBy(c => c.DisplayName, StringComparer.Ordinal)
                     .ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            entries.Add(new CategoryPlanEntry(CategoryPlanAction.Remove, category.Id, category.DisplayName));
        }

        return new CategoryPlan(appId, Requested: true, entries);
    }
}
