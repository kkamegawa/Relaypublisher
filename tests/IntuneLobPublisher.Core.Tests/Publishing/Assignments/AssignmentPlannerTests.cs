using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class AssignmentPlannerTests
{
    private const string AppId = "app-1";

    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid GroupB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

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
        bool isExclusion = false,
        string id = "assignment-1")
        => new(
            id,
            new AssignmentTargetKey(AssignmentTargetKind.Group, groupId, isExclusion),
            intent,
            filter,
            settings ?? NormalizedAssignmentSettings.Default);

    private static AssignmentPlanEntry Single(AssignmentPlan plan, AssignmentPlanAction action)
    {
        var matches = plan.Entries.Where(e => e.Action == action).ToList();
        Assert.HasCount(1, matches, $"Expected exactly one {action} entry.");
        return matches[0];
    }

    [TestMethod]
    public void CreatePlan_NewTarget_IsAdded()
    {
        var plan = AssignmentPlanner.CreatePlan(AppId, App(GroupAssignment(GroupA)), AssignmentSyncMode.Merge, []);

        var entry = Single(plan, AssignmentPlanAction.Add);
        Assert.AreEqual(new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, false), entry.Key);
        Assert.IsTrue(plan.HasChanges);
    }

    [TestMethod]
    public void CreatePlan_IdenticalAssignment_IsKept()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA)]);

        Single(plan, AssignmentPlanAction.Keep);
        Assert.IsFalse(plan.HasChanges);
    }

    [TestMethod]
    public void CreatePlan_IntentConflict_ManifestWins()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA, intent: "required")),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, intent: "available")]);

        var entry = Single(plan, AssignmentPlanAction.Update);
        Assert.AreEqual("required", entry.Desired!.Intent);
        Assert.ContainsSingle(entry.Changes);
        Assert.AreEqual("intent: available -> required", entry.Changes[0]);
    }

    [TestMethod]
    public void CreatePlan_FilterAdded_IsUpdate()
    {
        var manifest = GroupAssignment(GroupA);
        manifest.FilterId = FilterA.ToString();
        manifest.FilterMode = "include";

        var plan = AssignmentPlanner.CreatePlan(AppId, App(manifest), AssignmentSyncMode.Merge, [ExistingGroup(GroupA)]);

        var entry = Single(plan, AssignmentPlanAction.Update);
        Assert.AreEqual($"filter: none -> include {FilterA}", entry.Changes[0]);
    }

    [TestMethod]
    public void CreatePlan_FilterRemoved_IsUpdate()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, filter: new AssignmentFilter(FilterA, AssignmentFilterMode.Include))]);

        var entry = Single(plan, AssignmentPlanAction.Update);
        Assert.AreEqual($"filter: include {FilterA} -> none", entry.Changes[0]);
    }

    [TestMethod]
    public void CreatePlan_FilterModeChanged_IsUpdate()
    {
        var manifest = GroupAssignment(GroupA);
        manifest.FilterId = FilterA.ToString();
        manifest.FilterMode = "exclude";

        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(manifest),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, filter: new AssignmentFilter(FilterA, AssignmentFilterMode.Include))]);

        Single(plan, AssignmentPlanAction.Update);
    }

    [TestMethod]
    public void CreatePlan_SettingsChanged_IsUpdate()
    {
        var manifest = GroupAssignment(GroupA);
        manifest.Settings = new AssignmentSettingsManifest { Notifications = "hideAll", RestartGracePeriodMinutes = 1440 };

        var plan = AssignmentPlanner.CreatePlan(AppId, App(manifest), AssignmentSyncMode.Merge, [ExistingGroup(GroupA)]);

        var entry = Single(plan, AssignmentPlanAction.Update);
        CollectionAssert.AreEqual(
            new[]
            {
                "settings.notifications: showAll -> hideAll",
                "settings.restartGracePeriodMinutes: none -> 1440",
            },
            entry.Changes.ToArray());
    }

    [TestMethod]
    public void CreatePlan_OmittedSettingsVersusGraphDefaults_IsKeep()
    {
        // Manifest omits Settings entirely; Graph reports the default notification behavior.
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, settings: new NormalizedAssignmentSettings("showAll", null))]);

        Single(plan, AssignmentPlanAction.Keep);
        Assert.IsFalse(plan.HasChanges);
    }

    [TestMethod]
    public void CreatePlan_Merge_PreservesUnlistedAssignments()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupB, intent: "available", id: "assignment-b")]);

        Assert.HasCount(2, plan.Entries);
        var kept = Single(plan, AssignmentPlanAction.Keep);
        Assert.AreEqual(GroupB, kept.Key.GroupId);
        Assert.IsNull(kept.Desired);
    }

    [TestMethod]
    public void CreatePlan_Replace_RemovesUnlistedAssignments()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Replace,
            [ExistingGroup(GroupB, id: "assignment-b")]);

        var removed = Single(plan, AssignmentPlanAction.Remove);
        Assert.AreEqual(GroupB, removed.Key.GroupId);
        Assert.AreEqual("assignment-b", removed.Current!.Id);
    }

    [TestMethod]
    public void CreatePlan_ExclusionAndIncludeOfSameGroup_AreDistinctTargets()
    {
        var exclusion = new AssignmentManifest { GroupId = GroupA.ToString(), Mode = "exclude" };

        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(exclusion),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, intent: "required", isExclusion: false)]);

        // The include assignment stays untouched; the exclusion is a new, separate target.
        Single(plan, AssignmentPlanAction.Add);
        Single(plan, AssignmentPlanAction.Keep);
    }

    [TestMethod]
    public void CreatePlan_BuiltInTargets_MatchByKind()
    {
        var app = App(
            new AssignmentManifest { Target = "allDevices", Intent = "required" },
            new AssignmentManifest { Target = "allLicensedUsers", Intent = "available" });
        var current = new CurrentAssignment(
            "assignment-ad",
            new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, false),
            "required",
            null,
            NormalizedAssignmentSettings.Default);

        var plan = AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, [current]);

        Single(plan, AssignmentPlanAction.Keep);
        var added = Single(plan, AssignmentPlanAction.Add);
        Assert.AreEqual(AssignmentTargetKind.AllLicensedUsers, added.Key.Kind);
    }

    [TestMethod]
    public void CreatePlan_MacOsPkgWithUninstallIntent_Throws()
    {
        var app = App(GroupAssignment(GroupA, intent: "uninstall"));
        app.Platform = "macos";
        app.AppType = null; // pkg is the macOS default

        Assert.ThrowsExactly<AssignmentPlanningException>(
            () => AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, []));
    }

    [TestMethod]
    public void CreatePlan_MacOsLobWithUninstallIntent_IsAllowed()
    {
        var app = App(GroupAssignment(GroupA, intent: "uninstall"));
        app.Platform = "macos";
        app.AppType = "lob";

        var plan = AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, []);

        Single(plan, AssignmentPlanAction.Add);
    }

    [TestMethod]
    public void CreatePlan_DuplicateManifestTargets_Throws()
    {
        var app = App(GroupAssignment(GroupA), GroupAssignment(GroupA, intent: "available"));

        Assert.ThrowsExactly<AssignmentPlanningException>(
            () => AssignmentPlanner.CreatePlan(AppId, app, AssignmentSyncMode.Merge, []));
    }

    [TestMethod]
    public void CreatePlan_DuplicateCurrentTargets_Throws()
    {
        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentPlanner.CreatePlan(
            AppId,
            App(GroupAssignment(GroupA)),
            AssignmentSyncMode.Merge,
            [ExistingGroup(GroupA, id: "a-1"), ExistingGroup(GroupA, intent: "available", id: "a-2")]));
    }

    [TestMethod]
    public void CreatePlan_EmptyManifestWithReplace_RemovesEverything()
    {
        var plan = AssignmentPlanner.CreatePlan(
            AppId,
            App(),
            AssignmentSyncMode.Replace,
            [ExistingGroup(GroupA), ExistingGroup(GroupB, id: "assignment-b")]);

        Assert.HasCount(2, plan.Entries);
        Assert.IsTrue(plan.Entries.All(e => e.Action == AssignmentPlanAction.Remove));
    }
}
