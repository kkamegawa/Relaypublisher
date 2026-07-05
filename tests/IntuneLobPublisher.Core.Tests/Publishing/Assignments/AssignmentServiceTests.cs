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

    private sealed class FakeClient : IGraphAppAssignmentClient
    {
        public IReadOnlyList<CurrentAssignment> Current { get; init; } = [];

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<CurrentAssignment>> GetAssignmentsAsync(string appId, CancellationToken cancellationToken)
        {
            Calls.Add($"get {appId}");
            return Task.FromResult(Current);
        }

        public Task CreateAssignmentAsync(string appId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken)
        {
            Calls.Add($"create {assignment.Key} isWin32={isWin32}");
            return Task.CompletedTask;
        }

        public Task UpdateAssignmentAsync(string appId, string assignmentId, DesiredAssignment assignment, bool isWin32, CancellationToken cancellationToken)
        {
            Calls.Add($"update {assignmentId} {assignment.Key} intent={assignment.Intent}");
            return Task.CompletedTask;
        }

        public Task DeleteAssignmentAsync(string appId, string assignmentId, CancellationToken cancellationToken)
        {
            Calls.Add($"delete {assignmentId}");
            return Task.CompletedTask;
        }
    }

    private static AssignmentService CreateService(FakeClient client)
        => new(client, NullLogger<AssignmentService>.Instance);

    private static AppManifest App(params AssignmentManifest[] assignments)
    {
        var app = TestManifests.CreateValidApp();
        app.Assignments = [.. assignments];
        return app;
    }

    private static AssignmentManifest GroupAssignment(Guid groupId, string intent = "required")
        => new() { Target = "group", GroupId = groupId.ToString(), Intent = intent };

    private static CurrentAssignment ExistingGroup(Guid groupId, string intent = "required", string id = "a-1")
        => new(
            id,
            new AssignmentTargetKey(AssignmentTargetKind.Group, groupId, false),
            intent,
            null,
            NormalizedAssignmentSettings.Default);

    [TestMethod]
    public async Task CreatePlanAsync_ReadsCurrentAssignmentsAndComputesPlan()
    {
        var client = new FakeClient { Current = [ExistingGroup(GroupB, id: "a-b")] };
        var service = CreateService(client);

        var plan = await service.CreatePlanAsync(AppId, App(GroupAssignment(GroupA)), AssignmentSyncMode.Merge, CancellationToken.None);

        Assert.AreEqual($"get {AppId}", client.Calls.Single());
        Assert.HasCount(2, plan.Entries);
        Assert.IsTrue(plan.HasChanges);
    }

    [TestMethod]
    public async Task ApplyAsync_Merge_NeverTouchesUnlistedAssignments()
    {
        var client = new FakeClient { Current = [ExistingGroup(GroupB, intent: "available", id: "a-b")] };
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA));
        var plan = await service.CreatePlanAsync(AppId, app, AssignmentSyncMode.Merge, CancellationToken.None);

        await service.ApplyAsync(plan, app, CancellationToken.None);

        // One create for the manifest group; the unlisted assignment gets no update/delete call.
        CollectionAssert.AreEqual(
            new[] { $"get {AppId}", $"create group {GroupA} isWin32=True" },
            client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_Replace_DeletesUnlistedAssignments()
    {
        var client = new FakeClient { Current = [ExistingGroup(GroupB, id: "a-b")] };
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA));
        var plan = await service.CreatePlanAsync(AppId, app, AssignmentSyncMode.Replace, CancellationToken.None);

        await service.ApplyAsync(plan, app, CancellationToken.None);

        CollectionAssert.Contains(client.Calls, "delete a-b");
    }

    [TestMethod]
    public async Task ApplyAsync_IntentConflict_UpdatesToManifestValue()
    {
        var client = new FakeClient { Current = [ExistingGroup(GroupA, intent: "available", id: "a-a")] };
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA, intent: "required"));
        var plan = await service.CreatePlanAsync(AppId, app, AssignmentSyncMode.Merge, CancellationToken.None);

        await service.ApplyAsync(plan, app, CancellationToken.None);

        CollectionAssert.Contains(client.Calls, $"update a-a group {GroupA} intent=required");
    }

    [TestMethod]
    public async Task ApplyAsync_NoChanges_MakesNoGraphCalls()
    {
        var client = new FakeClient { Current = [ExistingGroup(GroupA, id: "a-a")] };
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA));
        var plan = await service.CreatePlanAsync(AppId, app, AssignmentSyncMode.Merge, CancellationToken.None);
        client.Calls.Clear();

        await service.ApplyAsync(plan, app, CancellationToken.None);

        Assert.IsEmpty(client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_NonWin32App_PassesIsWin32False()
    {
        var client = new FakeClient();
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA));
        app.Platform = "macos";
        app.AppType = "lob";
        app.InstallerType = "pkg";
        var plan = AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, []);

        await service.ApplyAsync(plan, app, CancellationToken.None);

        CollectionAssert.Contains(client.Calls, $"create group {GroupA} isWin32=False");
    }

    [TestMethod]
    public async Task ApplyAsync_MacOsPkgUninstall_ThrowsBeforeAnyWrite()
    {
        var client = new FakeClient();
        var service = CreateService(client);
        var app = App(GroupAssignment(GroupA, intent: "uninstall"));
        app.Platform = "macos";
        app.AppType = null;

        // Build a plan against a lob app so the planner guard passes, then apply with the pkg app.
        var lobApp = App(GroupAssignment(GroupA, intent: "uninstall"));
        lobApp.Platform = "macos";
        lobApp.AppType = "lob";
        var plan = AssignmentPlanner.CreatePlan(AppId, lobApp, AssignmentSyncMode.Merge, []);

        await Assert.ThrowsExactlyAsync<AssignmentPlanningException>(
            () => service.ApplyAsync(plan, app, CancellationToken.None));
        Assert.IsEmpty(client.Calls);
    }

    [TestMethod]
    public async Task ApplyAsync_UpdateEntryWithoutAssignmentId_Throws()
    {
        var key = new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, false);
        var plan = new AssignmentPlan(AppId, AssignmentSyncMode.Merge,
        [
            new AssignmentPlanEntry(
                AssignmentPlanAction.Update,
                key,
                new DesiredAssignment(key, "required", null, NormalizedAssignmentSettings.Default),
                new CurrentAssignment(null, key, "available", null, NormalizedAssignmentSettings.Default),
                ["intent: available -> required"]),
        ]);
        var service = CreateService(new FakeClient());

        await Assert.ThrowsExactlyAsync<AssignmentPlanningException>(
            () => service.ApplyAsync(plan, App(GroupAssignment(GroupA)), CancellationToken.None));
    }
}
