using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

[TestClass]
public sealed class CategoryPlanFormatterTests
{
    [TestMethod]
    public void Format_RendersCountsAndOneLinePerEntry()
    {
        var plan = new CategoryPlan(
            "app-1",
            Requested: true,
            [
                new CategoryPlanEntry(CategoryPlanAction.Add, "cat-business", "Business Apps"),
                new CategoryPlanEntry(CategoryPlanAction.Keep, "cat-productivity", "Productivity"),
                new CategoryPlanEntry(CategoryPlanAction.Remove, "cat-legacy", "Legacy"),
            ]);

        var text = CategoryPlanFormatter.Format(plan);

        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual("Category plan for app app-1: 1 add, 1 keep, 1 remove", lines[0]);
        Assert.AreEqual("  + Business Apps (cat-business)", lines[1]);
        Assert.AreEqual("  = Productivity (cat-productivity)", lines[2]);
        Assert.AreEqual("  - Legacy (cat-legacy)", lines[3]);
    }

    [TestMethod]
    public void Format_NewAppPlan_ShowsThePlaceholderId()
    {
        var plan = new CategoryPlan(
            "(new app)",
            Requested: true,
            [new CategoryPlanEntry(CategoryPlanAction.Add, "cat-business", "Business Apps")]);

        StringAssert.StartsWith(CategoryPlanFormatter.Format(plan), "Category plan for app (new app): 1 add");
    }

    [TestMethod]
    public void Format_NotRequestedPlan_IsEmpty()
    {
        Assert.AreEqual(string.Empty, CategoryPlanFormatter.Format(CategoryPlan.NotRequested("app-1")));
    }

    [TestMethod]
    public void Format_IsDeterministic()
    {
        var plan = new CategoryPlan(
            "app-1",
            Requested: true,
            [new CategoryPlanEntry(CategoryPlanAction.Add, "cat-business", "Business Apps")]);

        Assert.AreEqual(CategoryPlanFormatter.Format(plan), CategoryPlanFormatter.Format(plan));
    }
}
