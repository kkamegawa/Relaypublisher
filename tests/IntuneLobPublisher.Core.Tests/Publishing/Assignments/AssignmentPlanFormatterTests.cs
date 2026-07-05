using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class AssignmentPlanFormatterTests
{
    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid GroupB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    [TestMethod]
    public void Format_MixedPlan_ProducesDeterministicText()
    {
        var addKey = new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, false);
        var updateKey = new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, false);
        var keepKey = new AssignmentTargetKey(AssignmentTargetKind.Group, GroupB, true);

        var plan = new AssignmentPlan("app-1", AssignmentSyncMode.Merge,
        [
            new AssignmentPlanEntry(
                AssignmentPlanAction.Add,
                addKey,
                new DesiredAssignment(addKey, "required", new AssignmentFilter(FilterA, AssignmentFilterMode.Include), NormalizedAssignmentSettings.Default),
                null,
                []),
            new AssignmentPlanEntry(
                AssignmentPlanAction.Update,
                updateKey,
                new DesiredAssignment(updateKey, "required", null, NormalizedAssignmentSettings.Default),
                new CurrentAssignment("a-2", updateKey, "available", null, NormalizedAssignmentSettings.Default),
                ["intent: available -> required"]),
            new AssignmentPlanEntry(
                AssignmentPlanAction.Keep,
                keepKey,
                null,
                new CurrentAssignment("a-3", keepKey, "required", null, NormalizedAssignmentSettings.Default),
                []),
        ]);

        var text = AssignmentPlanFormatter.Format(plan);

        var expected =
            $"Assignment plan for app app-1 (sync: merge): 1 add, 1 update, 1 keep, 0 remove{Environment.NewLine}" +
            $"  + group {GroupA}: intent=required, filter=include {FilterA}{Environment.NewLine}" +
            $"  ~ allDevices: intent: available -> required{Environment.NewLine}" +
            $"  = group {GroupB} (exclude): intent=required{Environment.NewLine}";
        Assert.AreEqual(expected, text);
    }

    [TestMethod]
    public void Format_RemoveEntry_ShowsRemovedState()
    {
        var key = new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, false);
        var plan = new AssignmentPlan("app-1", AssignmentSyncMode.Replace,
        [
            new AssignmentPlanEntry(
                AssignmentPlanAction.Remove,
                key,
                null,
                new CurrentAssignment("a-1", key, "available", null, new NormalizedAssignmentSettings("hideAll", 60)),
                []),
        ]);

        var text = AssignmentPlanFormatter.Format(plan);

        StringAssert.Contains(text, "(sync: replace): 0 add, 0 update, 0 keep, 1 remove");
        StringAssert.Contains(text, $"  - group {GroupA}: intent=available, notifications=hideAll, restartGracePeriodMinutes=60");
    }
}
