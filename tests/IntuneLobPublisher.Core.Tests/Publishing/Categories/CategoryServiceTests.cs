using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Categories;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

[TestClass]
public sealed class CategoryServiceTests
{
    private const string AppId = "app-1";

    private sealed class FakeCategoryGraphClient : ICategoryGraphClient
    {
        public List<string> Calls { get; } = [];

        public List<IntuneAppCategory> TenantCategories { get; } =
        [
            new("cat-business", "Business Apps"),
            new("cat-productivity", "Productivity"),
            new("cat-legacy", "Legacy"),
        ];

        public List<IntuneAppCategory> AppCategories { get; } = [];

        public Exception? TenantListException { get; set; }

        public Exception? AddException { get; set; }

        public Task<IReadOnlyList<IntuneAppCategory>> ListTenantCategoriesAsync(CancellationToken cancellationToken)
        {
            Calls.Add("list tenant");
            return TenantListException is null
                ? Task.FromResult<IReadOnlyList<IntuneAppCategory>>(TenantCategories)
                : Task.FromException<IReadOnlyList<IntuneAppCategory>>(TenantListException);
        }

        public Task<IReadOnlyList<IntuneAppCategory>> ListAppCategoriesAsync(string appId, CancellationToken cancellationToken)
        {
            Calls.Add($"list app {appId}");
            return Task.FromResult<IReadOnlyList<IntuneAppCategory>>(AppCategories);
        }

        public Task<bool> AddCategoryAsync(string appId, string categoryId, CancellationToken cancellationToken)
        {
            Calls.Add($"add {appId} {categoryId}");
            return AddException is null ? Task.FromResult(true) : Task.FromException<bool>(AddException);
        }

        public Task<bool> RemoveCategoryAsync(string appId, string categoryId, CancellationToken cancellationToken)
        {
            Calls.Add($"remove {appId} {categoryId}");
            return Task.FromResult(true);
        }
    }

    private static (CategoryService Service, FakeCategoryGraphClient Client) CreateService()
    {
        var client = new FakeCategoryGraphClient();
        return (new CategoryService(client, NullLogger<CategoryService>.Instance), client);
    }

    private static AppManifest WindowsApp(List<string>? categories)
    {
        var app = TestManifests.CreateValidApp();
        app.Categories = categories;
        return app;
    }

    [TestMethod]
    public async Task CreatePlanAsync_CategoriesOmitted_MakesNoGraphCall()
    {
        var (service, client) = CreateService();

        var plan = await service.CreatePlanAsync(AppId, WindowsApp(null), CancellationToken.None);

        Assert.IsFalse(plan.Requested);
        Assert.IsEmpty(client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_NotRequestedPlan_MakesNoGraphCall()
    {
        var (service, client) = CreateService();

        await service.ApplyAsync(CategoryPlan.NotRequested(AppId), CancellationToken.None);

        Assert.IsEmpty(client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_NewAppPlaceholder_ThrowsBeforeGraphWrite()
    {
        var (service, client) = CreateService();
        var plan = new CategoryPlan(
            PublishOrchestrator.NewAppPlaceholderId,
            Requested: true,
            [new CategoryPlanEntry(CategoryPlanAction.Add, "cat-business", "Business Apps")]);

        var exception = await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => service.ApplyAsync(plan, CancellationToken.None));

        StringAssert.Contains(exception.Message, "before the Intune app has been created");
        Assert.IsEmpty(client.Calls);
    }

    [TestMethod]
    public async Task CreatePlanAsync_ExistingApp_ReadsTenantCatalogThenAppRelationships()
    {
        var (service, client) = CreateService();
        client.AppCategories.Add(new IntuneAppCategory("cat-legacy", "Legacy"));

        var plan = await service.CreatePlanAsync(AppId, WindowsApp(["Business Apps"]), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "list tenant", $"list app {AppId}" }, client.Calls);
        CollectionAssert.AreEqual(
            new[] { CategoryPlanAction.Add, CategoryPlanAction.Remove },
            plan.Entries.Select(e => e.Action).ToList());
    }

    [TestMethod]
    public async Task CreatePlanAsync_NewApp_SkipsThePerAppReadAndPlansAllAdds()
    {
        var (service, client) = CreateService();

        var plan = await service.CreatePlanAsync(null, WindowsApp(["Business Apps"]), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "list tenant" }, client.Calls);
        Assert.AreEqual(PublishOrchestrator.NewAppPlaceholderId, plan.AppId);
        Assert.AreEqual(CategoryPlanAction.Add, plan.Entries.Single().Action);
    }

    [TestMethod]
    public async Task CreatePlanAsync_MissingCategory_ThrowsBeforeReadingTheAppRelationships()
    {
        var (service, client) = CreateService();

        await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => service.CreatePlanAsync(AppId, WindowsApp(["Missing"]), CancellationToken.None));

        CollectionAssert.AreEqual(new[] { "list tenant" }, client.Calls);
    }

    [TestMethod]
    public async Task CreatePlanAsync_TenantListingAccessDenied_KeepsTheIdentityWideException()
    {
        var (service, client) = CreateService();
        client.TenantListException = new GraphAccessDeniedException("Forbidden.", 403, null, null, "Forbidden");

        await Assert.ThrowsExactlyAsync<GraphAccessDeniedException>(
            () => service.CreatePlanAsync(AppId, WindowsApp(["Business Apps"]), CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePlanAsync_TenantListingGraphFailure_BecomesCategorySyncException()
    {
        var (service, client) = CreateService();
        client.TenantListException = new GraphRequestException("Boom.", 500, null, null);

        var exception = await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => service.CreatePlanAsync(AppId, WindowsApp(["Business Apps"]), CancellationToken.None));

        Assert.IsInstanceOfType<GraphRequestException>(exception.InnerException);
    }

    [TestMethod]
    public async Task ApplyAsync_AppliesAddsBeforeRemoves()
    {
        var (service, client) = CreateService();
        client.AppCategories.Add(new IntuneAppCategory("cat-legacy", "Legacy"));
        var app = WindowsApp(["Business Apps"]);
        var plan = await service.CreatePlanAsync(AppId, app, CancellationToken.None);
        client.Calls.Clear();

        await service.ApplyAsync(plan, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { $"add {AppId} cat-business", $"remove {AppId} cat-legacy" }, client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_KeepEntries_AreNotWritten()
    {
        var (service, client) = CreateService();
        client.AppCategories.Add(new IntuneAppCategory("cat-business", "Business Apps"));
        var app = WindowsApp(["Business Apps"]);
        var plan = await service.CreatePlanAsync(AppId, app, CancellationToken.None);
        client.Calls.Clear();

        await service.ApplyAsync(plan, CancellationToken.None);

        Assert.IsEmpty(client.Calls);
        Assert.IsFalse(plan.HasChanges);
    }

    [TestMethod]
    public async Task ApplyAsync_GraphFailure_BecomesCategorySyncException()
    {
        var (service, client) = CreateService();
        client.AddException = new GraphRequestException("Boom.", 500, null, null);
        var app = WindowsApp(["Business Apps"]);
        var plan = await service.CreatePlanAsync(AppId, app, CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<CategorySyncException>(
            () => service.ApplyAsync(plan, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Business Apps");
    }

    [TestMethod]
    public async Task ApplyAsync_AfterAPartialFailure_TheNextRunConverges()
    {
        // First run: the remove fails after the add succeeded. Second run replans from Graph's current
        // state, so only the outstanding remove is left and the app converges.
        var (service, client) = CreateService();
        client.AppCategories.Add(new IntuneAppCategory("cat-legacy", "Legacy"));
        var app = WindowsApp(["Business Apps"]);

        var firstPlan = await service.CreatePlanAsync(AppId, app, CancellationToken.None);
        Assert.AreEqual(2, firstPlan.Entries.Count);

        client.AppCategories.Add(new IntuneAppCategory("cat-business", "Business Apps"));
        client.Calls.Clear();

        var secondPlan = await service.CreatePlanAsync(AppId, app, CancellationToken.None);
        await service.ApplyAsync(secondPlan, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { CategoryPlanAction.Keep, CategoryPlanAction.Remove },
            secondPlan.Entries.Select(e => e.Action).ToList());
        Assert.IsTrue(client.Calls.Contains($"remove {AppId} cat-legacy"));
        Assert.IsFalse(client.Calls.Any(c => c.StartsWith("add ")));
    }
}
