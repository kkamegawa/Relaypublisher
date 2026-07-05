using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing.Assignments;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class AssignmentServiceTests
{
    private const string AppId = "app-1";

    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid GroupB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private sealed class FakeAssignmentGraphClient : IAssignmentGraphClient
    {
        public IReadOnlyList<CurrentAssignment> CurrentAssignments { get; set; } = [];

        public List<DesiredAssignment> Created { get; } = [];

        public List<(CurrentAssignment Current, DesiredAssignment Desired)> Updated { get; } = [];

        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<CurrentAssignment>> ListAssignmentsAsync(string appId, CancellationToken cancellationToken)
            => Task.FromResult(CurrentAssignments);

        public Task<string> CreateAssignmentAsync(string appId, DesiredAssignment assignment, CancellationToken cancellationToken)
        {
            Created.Add(assignment);
            return Task.FromResult("created-assignment");
        }

        public Task UpdateAssignmentAsync(string appId, CurrentAssignment current, DesiredAssignment desired, CancellationToken cancellationToken)
        {
            Updated.Add((current, desired));
            return Task.CompletedTask;
        }

        public Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken)
        {
            Deleted.Add(assignmentId);
            return Task.CompletedTask;
        }
    }

    private static AssignmentService Service(FakeAssignmentGraphClient client)
        => new(client, NullLogger<AssignmentService>.Instance);

    private static AppManifest App(params AssignmentManifest[] assignments)
    {
        var app = TestManifests.CreateValidApp();
        app.Assignments = [.. assignments];
        return app;
    }

    private static AssignmentManifest GroupAssignment(Guid groupId, string intent = "required")
        => new() { Target = "group", GroupId = groupId.ToString(), Intent = intent };

    private static CurrentAssignment ExistingGroup(
        Guid groupId,
        string intent = "required",
        AssignmentFilter? filter = null,
        NormalizedAssignmentSettings? settings = null,
        string id = "assignment-1")
        => new(
            id,
            new AssignmentTargetKey(AssignmentTargetKind.Group, groupId, IsExclusion: false),
            intent,
            filter,
            settings ?? NormalizedAssignmentSettings.Default);

    [TestMethod]
    public async Task CreatePlanAsync_MergePreservesUnlistedAssignmentsAndApplyDoesNotDelete()
    {
        var client = new FakeAssignmentGraphClient
        {
            CurrentAssignments = [ExistingGroup(GroupB, id: "assignment-b")],
        };
        var service = Service(client);

        var plan = await service.CreatePlanAsync(AppId, App(GroupAssignment(GroupA)), AssignmentSyncMode.Merge, CancellationToken.None);
        await service.ApplyAsync(plan, App(GroupAssignment(GroupA)), CancellationToken.None);

        Assert.HasCount(1, client.Created);
        Assert.HasCount(0, client.Updated);
        Assert.HasCount(0, client.Deleted);
    }

    [TestMethod]
    public async Task CreatePlanAsync_ReplaceRemovesUnlistedAssignments()
    {
        var client = new FakeAssignmentGraphClient
        {
            CurrentAssignments = [ExistingGroup(GroupB, id: "assignment-b")],
        };
        var service = Service(client);

        var plan = await service.CreatePlanAsync(AppId, App(), AssignmentSyncMode.Replace, CancellationToken.None);
        await service.ApplyAsync(plan, App(), CancellationToken.None);

        Assert.HasCount(0, client.Created);
        Assert.HasCount(0, client.Updated);
        CollectionAssert.AreEqual(new[] { "assignment-b" }, client.Deleted.ToArray());
    }

    [TestMethod]
    public async Task ApplyAsync_KeepEntryDoesNotCallGraph()
    {
        var client = new FakeAssignmentGraphClient();
        var app = App(GroupAssignment(GroupA));
        var plan = AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, [ExistingGroup(GroupA)]);

        await Service(client).ApplyAsync(plan, app, CancellationToken.None);

        Assert.HasCount(0, client.Created);
        Assert.HasCount(0, client.Updated);
        Assert.HasCount(0, client.Deleted);
    }

    [TestMethod]
    public async Task ApplyAsync_UpdateEntryPassesDesiredIntentSettingsAndFilter()
    {
        var client = new FakeAssignmentGraphClient();
        var appAssignment = GroupAssignment(GroupA, intent: "required");
        appAssignment.FilterId = FilterA.ToString();
        appAssignment.FilterMode = "include";
        appAssignment.Settings = new AssignmentSettingsManifest { Notifications = "hideAll", RestartGracePeriodMinutes = 1440 };
        var app = App(appAssignment);
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            app,
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, intent: "available", id: "assignment-a")]);

        await Service(client).ApplyAsync(plan, app, CancellationToken.None);

        Assert.HasCount(1, client.Updated);
        Assert.AreEqual("assignment-a", client.Updated[0].Current.Id);
        Assert.AreEqual("required", client.Updated[0].Desired.Intent);
        Assert.AreEqual(new AssignmentFilter(FilterA, AssignmentFilterMode.Include), client.Updated[0].Desired.Filter);
        Assert.AreEqual(new NormalizedAssignmentSettings("hideAll", 1440), client.Updated[0].Desired.Settings);
    }

    [TestMethod]
    public async Task ApplyAsync_MacOsPkgWithUninstallIntent_ThrowsBeforeGraphCall()
    {
        var client = new FakeAssignmentGraphClient();
        var app = App(GroupAssignment(GroupA, intent: "uninstall"));
        app.Platform = "macos";
        app.AppType = null;
        var plan = new AssignmentPlan(
            AppId,
            AssignmentSyncMode.Merge,
            [new AssignmentPlanEntry(
                AssignmentPlanAction.Add,
                new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false),
                new DesiredAssignment(
                    new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false),
                    "uninstall",
                    null,
                    NormalizedAssignmentSettings.Default),
                null,
                [])]);

        await Assert.ThrowsExactlyAsync<AssignmentPlanningException>(
            () => Service(client).ApplyAsync(plan, app, CancellationToken.None));

        Assert.HasCount(0, client.Created);
    }
}
