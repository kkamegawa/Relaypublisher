using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

[TestClass]
public sealed class CategoryPlannerTests
{
    private static readonly IntuneAppCategory Business = new("cat-business", "Business Apps");
    private static readonly IntuneAppCategory Productivity = new("cat-productivity", "Productivity");
    private static readonly IntuneAppCategory Legacy = new("cat-legacy", "Legacy");

    [TestMethod]
    public void Resolve_SingleCaseInsensitiveMatch_ResolvesTheId()
    {
        var resolved = CategoryNameResolver.Resolve(["business APPS"], [Business, Productivity]);

        Assert.AreEqual("cat-business", resolved.Single().Id);
        Assert.AreEqual("Business Apps", resolved.Single().DisplayName, "The plan shows Graph's spelling.");
    }

    [TestMethod]
    public void Resolve_PreservesManifestOrder()
    {
        var resolved = CategoryNameResolver.Resolve(["Productivity", "Business Apps"], [Business, Productivity]);

        CollectionAssert.AreEqual(
            new[] { "Productivity", "Business Apps" }, resolved.Select(c => c.DisplayName).ToList());
    }

    [TestMethod]
    public void Resolve_NoMatch_ThrowsCategorySyncException()
    {
        var exception = Assert.ThrowsExactly<CategorySyncException>(
            () => CategoryNameResolver.Resolve(["Missing"], [Business]));

        StringAssert.Contains(exception.Message, "does not exist in the tenant");
    }

    [TestMethod]
    public void Resolve_SeveralMatches_ThrowsCategorySyncException()
    {
        var duplicates = new[] { Business, new IntuneAppCategory("cat-other", "business apps") };

        var exception = Assert.ThrowsExactly<CategorySyncException>(
            () => CategoryNameResolver.Resolve(["Business Apps"], duplicates));

        StringAssert.Contains(exception.Message, "matches 2 tenant categories");
    }

    [TestMethod]
    public void CreatePlan_ExactSet_AddsMissingKeepsPresentRemovesUnlisted()
    {
        var plan = CategoryPlanner.CreatePlan("app-1", [Business, Productivity], [Productivity, Legacy]);

        CollectionAssert.AreEqual(
            new[]
            {
                (CategoryPlanAction.Add, "Business Apps"),
                (CategoryPlanAction.Keep, "Productivity"),
                (CategoryPlanAction.Remove, "Legacy"),
            },
            plan.Entries.Select(e => (e.Action, e.DisplayName)).ToList());
        Assert.IsTrue(plan.HasChanges);
        Assert.IsTrue(plan.Requested);
    }

    [TestMethod]
    public void CreatePlan_EmptyDesiredSet_RemovesEverything()
    {
        var plan = CategoryPlanner.CreatePlan("app-1", [], [Business, Legacy]);

        Assert.IsTrue(plan.Entries.All(e => e.Action == CategoryPlanAction.Remove));
        Assert.AreEqual(2, plan.Entries.Count);
    }

    [TestMethod]
    public void CreatePlan_NoCurrentRelationships_IsAllAdds()
    {
        var plan = CategoryPlanner.CreatePlan(PublishOrchestratorPlaceholder, [Business], []);

        Assert.AreEqual(CategoryPlanAction.Add, plan.Entries.Single().Action);
        Assert.AreEqual(PublishOrchestratorPlaceholder, plan.AppId);
    }

    private const string PublishOrchestratorPlaceholder = "(new app)";

    [TestMethod]
    public void CreatePlan_AlreadyConverged_HasNoChanges()
    {
        var plan = CategoryPlanner.CreatePlan("app-1", [Business], [Business]);

        Assert.IsFalse(plan.HasChanges);
        Assert.AreEqual(CategoryPlanAction.Keep, plan.Entries.Single().Action);
    }

    [TestMethod]
    public void CreatePlan_RemovesAreOrderedDeterministically()
    {
        var current = new[]
        {
            new IntuneAppCategory("cat-z", "Zeta"),
            new IntuneAppCategory("cat-a", "Alpha"),
            new IntuneAppCategory("cat-m", "Mu"),
        };

        var plan = CategoryPlanner.CreatePlan("app-1", [], current);

        CollectionAssert.AreEqual(
            new[] { "Alpha", "Mu", "Zeta" }, plan.Entries.Select(e => e.DisplayName).ToList());
    }

    [TestMethod]
    public void NotRequested_HasNoEntriesAndNoChanges()
    {
        var plan = CategoryPlan.NotRequested("app-1");

        Assert.IsFalse(plan.Requested);
        Assert.IsFalse(plan.HasChanges);
        Assert.IsEmpty(plan.Entries);
    }
}
