using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// <see cref="PublishCommand.SelectHighestVersions"/> keys entries by <c>AppIdentity</c>, which now
/// resolves macOS Architecture through <c>AppArchitecture.Resolve</c> (issue #123) instead of reading the
/// raw manifest field. These pin that an omitted macOS Architecture folds together with an explicit
/// "universal" declaration, while cross-identity migration aliases sharing one DisplayName collapse by
/// version before Graph access. Explicit x64/arm64 entries without a universal alias stay distinct.
/// </summary>
[TestClass]
public sealed class PublishCommandSelectHighestVersionsTests
{
    private static LoadedManifest MacOsManifest(
        string path,
        string? architecture,
        string packageVersion = "1.2.3",
        string? displayName = null)
    {
        var manifest = TestManifests.CreateValid();
        manifest.PackageVersion = packageVersion;
        manifest.Apps = [TestManifests.CreateValidMacOsApp(architecture, displayName: displayName)];
        return new LoadedManifest(path, manifest);
    }

    [TestMethod]
    public void SelectHighestVersions_OmittedAndExplicitUniversalArchitecture_FoldIntoOneEntry()
    {
        var manifests = new[]
        {
            MacOsManifest("manifests/a.yaml", architecture: null),
            MacOsManifest("manifests/b.yaml", architecture: "universal"),
        };

        var entries = PublishCommand.SelectHighestVersions(manifests);

        Assert.HasCount(1, entries);
    }

    [TestMethod]
    public void SelectHighestVersions_OmittedAndExplicitX64Architecture_StayDistinct()
    {
        var manifests = new[]
        {
            MacOsManifest("manifests/a.yaml", architecture: null),
            MacOsManifest("manifests/b.yaml", architecture: "x64"),
        };

        var entries = PublishCommand.SelectHighestVersions(manifests);

        Assert.HasCount(2, entries);
    }

    [TestMethod]
    public void SelectHighestVersions_OmittedMigrationAndHistoricalArm64_CollapsesToHighestVersionRegardlessOfOrder()
    {
        const string displayName = "Contoso Tool [macOS]";
        var newer = MacOsManifest(
            "manifests/contoso/1.10.0/tool.yaml", architecture: null, packageVersion: "1.10.0", displayName: displayName);
        var older = MacOsManifest(
            "manifests/contoso/1.9.0/tool.yaml", architecture: "arm64", packageVersion: "1.9.0", displayName: displayName);

        foreach (var manifests in new[] { new[] { newer, older }, new[] { older, newer } })
        {
            var entry = PublishCommand.SelectHighestVersions(manifests).Single();

            Assert.AreEqual("1.10.0", entry.Loaded.Manifest.PackageVersion);
            Assert.AreEqual("universal", AppArchitecture.Resolve(entry.App));
        }
    }

    [TestMethod]
    public void SelectHighestVersions_EqualVersionMigrationCollision_PrefersUniversal()
    {
        const string displayName = "Contoso Tool [macOS]";
        var entries = PublishCommand.SelectHighestVersions(
        [
            MacOsManifest("manifests/explicit.yaml", architecture: "arm64", displayName: displayName),
            MacOsManifest("manifests/omitted.yaml", architecture: null, displayName: displayName),
        ]);

        var entry = entries.Single();
        Assert.AreEqual("manifests/omitted.yaml", entry.Loaded.Path);
        Assert.AreEqual("universal", AppArchitecture.Resolve(entry.App));
    }

    [TestMethod]
    public void SelectHighestVersions_ExplicitX64AndArm64WithoutUniversal_StayDistinct()
    {
        const string displayName = "Contoso Tool [macOS]";
        var entries = PublishCommand.SelectHighestVersions(
        [
            MacOsManifest("manifests/x64.yaml", architecture: "x64", displayName: displayName),
            MacOsManifest("manifests/arm64.yaml", architecture: "arm64", displayName: displayName),
        ]);

        Assert.HasCount(2, entries);
    }

    [TestMethod]
    public void SelectHighestVersions_UniversalAndExplicitArchitectureWithDifferentDisplayNames_StayDistinct()
    {
        var entries = PublishCommand.SelectHighestVersions(
        [
            MacOsManifest("manifests/universal.yaml", architecture: null, displayName: "Contoso Tool [macOS]"),
            MacOsManifest("manifests/arm64.yaml", architecture: "arm64", displayName: "Contoso Tool [macOS Arm64]"),
        ]);

        Assert.HasCount(2, entries);
    }
}
