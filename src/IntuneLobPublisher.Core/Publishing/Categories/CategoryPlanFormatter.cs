using System.Text;

namespace IntuneLobPublisher.Core.Publishing.Categories;

/// <summary>
/// Renders a <see cref="CategoryPlan"/> as deterministic text for publish and dry-run output.
/// Only app ids, category ids and category display names appear - never secrets.
/// </summary>
public static class CategoryPlanFormatter
{
    public static string Format(CategoryPlan plan)
    {
        if (!plan.Requested)
        {
            // The manifest omitted Categories: there is no plan to show and nothing was read.
            return string.Empty;
        }

        var counts = new Dictionary<CategoryPlanAction, int>
        {
            [CategoryPlanAction.Add] = 0,
            [CategoryPlanAction.Keep] = 0,
            [CategoryPlanAction.Remove] = 0,
        };
        foreach (var entry in plan.Entries)
        {
            counts[entry.Action]++;
        }

        var builder = new StringBuilder();
        builder.Append($"Category plan for app {plan.AppId}: ");
        builder.AppendLine(
            $"{counts[CategoryPlanAction.Add]} add, {counts[CategoryPlanAction.Keep]} keep, {counts[CategoryPlanAction.Remove]} remove");

        foreach (var entry in plan.Entries)
        {
            var symbol = entry.Action switch
            {
                CategoryPlanAction.Add => "+",
                CategoryPlanAction.Remove => "-",
                _ => "=",
            };
            builder.AppendLine($"  {symbol} {entry.DisplayName} ({entry.CategoryId})");
        }

        return builder.ToString();
    }
}
