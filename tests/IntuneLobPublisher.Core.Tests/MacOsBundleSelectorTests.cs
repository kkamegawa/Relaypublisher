using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class MacOsBundleSelectorTests
{
    [TestMethod]
    public void ProjectPrimaryFirst_UniqueSegmentPrefixSelectsEntryAndPreservesManifestOrder()
    {
        var detection = new DetectionManifest
        {
            PrimaryBundleId = "com.contoso.product",
            IncludedApps =
            [
                new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "1.0" },
                new IncludedAppManifest { BundleId = "com.contoso.product.client", BundleVersion = "2.0" },
                new IncludedAppManifest { BundleId = "com.contoso.agent", BundleVersion = "1.5" },
            ],
        };

        var projected = MacOsBundleSelector.ProjectPrimaryFirst(detection);

        CollectionAssert.AreEqual(
            new[] { "com.contoso.product.client", "com.contoso.helper", "com.contoso.agent" },
            projected.Select(item => item.BundleId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "com.contoso.helper", "com.contoso.product.client", "com.contoso.agent" },
            detection.IncludedApps.Select(item => item.BundleId).ToArray());
    }

    [TestMethod]
    public void ProjectPrimaryFirst_OmittedSelectorReturnsIndependentFirstEntryProjection()
    {
        var detection = new DetectionManifest
        {
            IncludedApps =
            [
                new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "1.0" },
                new IncludedAppManifest { BundleId = "com.contoso.helper", BundleVersion = "1.0" },
            ],
        };

        var projected = MacOsBundleSelector.ProjectPrimaryFirst(detection);

        Assert.AreEqual("com.contoso.tool", projected[0].BundleId);
        Assert.AreNotSame(detection.IncludedApps, projected);
    }
}
