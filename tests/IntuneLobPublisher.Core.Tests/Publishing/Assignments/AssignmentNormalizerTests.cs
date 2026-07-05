using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class AssignmentNormalizerTests
{
    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private static AppManifest App(params AssignmentManifest[] assignments)
    {
        var app = TestManifests.CreateValidApp();
        app.Assignments = [.. assignments];
        return app;
    }

    [TestMethod]
    public void Normalize_DefaultsTargetToGroupAndModeToInclude()
    {
        var app = App(new AssignmentManifest { GroupId = GroupA.ToString(), Intent = "required" });

        var desired = AssignmentNormalizer.Normalize(app);

        Assert.HasCount(1, desired);
        Assert.AreEqual(new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false), desired[0].Key);
        Assert.AreEqual("required", desired[0].Intent);
        Assert.IsNull(desired[0].Filter);
        Assert.AreEqual(NormalizedAssignmentSettings.Default, desired[0].Settings);
    }

    [TestMethod]
    public void Normalize_GroupIdCasingDoesNotMatter()
    {
        var upper = App(new AssignmentManifest { GroupId = "00000000-0000-0000-0000-00000000000A", Intent = "required" });
        var lower = App(new AssignmentManifest { GroupId = "00000000-0000-0000-0000-00000000000a", Intent = "required" });

        Assert.AreEqual(
            AssignmentNormalizer.Normalize(upper)[0].Key,
            AssignmentNormalizer.Normalize(lower)[0].Key);
    }

    [TestMethod]
    [DataRow("allDevices", AssignmentTargetKind.AllDevices)]
    [DataRow("allLicensedUsers", AssignmentTargetKind.AllLicensedUsers)]
    public void Normalize_BuiltInTargets(string target, AssignmentTargetKind expectedKind)
    {
        var app = App(new AssignmentManifest { Target = target, Intent = "available" });

        var desired = AssignmentNormalizer.Normalize(app);

        Assert.AreEqual(new AssignmentTargetKey(expectedKind, null, IsExclusion: false), desired[0].Key);
    }

    [TestMethod]
    public void Normalize_ExclusionPinsIntentToRequired()
    {
        var app = App(new AssignmentManifest { GroupId = GroupA.ToString(), Mode = "exclude" });

        var desired = AssignmentNormalizer.Normalize(app);

        Assert.IsTrue(desired[0].Key.IsExclusion);
        Assert.AreEqual("required", desired[0].Intent);
    }

    [TestMethod]
    public void Normalize_ExclusionOnBuiltInTarget_Throws()
    {
        var app = App(new AssignmentManifest { Target = "allDevices", Mode = "exclude" });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void Normalize_UnknownMode_Throws()
    {
        var app = App(new AssignmentManifest { GroupId = GroupA.ToString(), Intent = "required", Mode = "sometimes" });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void Normalize_FilterIsParsed()
    {
        var app = App(new AssignmentManifest
        {
            GroupId = GroupA.ToString(),
            Intent = "required",
            FilterId = FilterA.ToString(),
            FilterMode = "exclude",
        });

        var desired = AssignmentNormalizer.Normalize(app);

        Assert.AreEqual(new AssignmentFilter(FilterA, AssignmentFilterMode.Exclude), desired[0].Filter);
    }

    [TestMethod]
    public void Normalize_OmittedSettingsUseDefaults()
    {
        var app = App(new AssignmentManifest { GroupId = GroupA.ToString(), Intent = "required" });

        Assert.AreEqual(NormalizedAssignmentSettings.Default, AssignmentNormalizer.Normalize(app)[0].Settings);
    }

    [TestMethod]
    public void Normalize_PartialSettingsFillMissingFieldsWithDefaults()
    {
        var app = App(new AssignmentManifest
        {
            GroupId = GroupA.ToString(),
            Intent = "required",
            Settings = new AssignmentSettingsManifest { RestartGracePeriodMinutes = 1440 },
        });

        var settings = AssignmentNormalizer.Normalize(app)[0].Settings;

        Assert.AreEqual("showAll", settings.Notifications);
        Assert.AreEqual(1440, settings.RestartGracePeriodMinutes);
    }

    [TestMethod]
    public void Normalize_MissingGroupId_Throws()
    {
        var app = App(new AssignmentManifest { Intent = "required" });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void Normalize_InvalidGroupIdGuid_Throws()
    {
        var app = App(new AssignmentManifest { GroupId = "not-a-guid", Intent = "required" });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void Normalize_MissingIntentOnIncludeAssignment_Throws()
    {
        var app = App(new AssignmentManifest { GroupId = GroupA.ToString() });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void Normalize_FilterWithoutMode_Throws()
    {
        var app = App(new AssignmentManifest
        {
            GroupId = GroupA.ToString(),
            Intent = "required",
            FilterId = FilterA.ToString(),
        });

        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentNormalizer.Normalize(app));
    }

    [TestMethod]
    public void ParseSyncMode_NullDefaultsToMerge()
    {
        Assert.AreEqual(AssignmentSyncMode.Merge, AssignmentSyncModes.Parse(null));
        Assert.AreEqual(AssignmentSyncMode.Merge, AssignmentSyncModes.Parse("merge"));
        Assert.AreEqual(AssignmentSyncMode.Replace, AssignmentSyncModes.Parse("replace"));
        Assert.ThrowsExactly<AssignmentPlanningException>(() => AssignmentSyncModes.Parse("sync"));
    }
}
