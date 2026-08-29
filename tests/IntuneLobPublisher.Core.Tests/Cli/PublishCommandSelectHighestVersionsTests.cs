using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// <see cref="PublishCommand.SelectHighestVersions"/> keys entries by <c>AppIdentity</c>, which now
/// resolves macOS Architecture through <c>AppArchitecture.Resolve</c> (issue #123) instead of reading the
/// raw manifest field. These pin that an omitted macOS Architecture folds together with an explicit
/// "universal" declaration, and stays distinct from an explicit "x64"/"arm64" declaration.
/// </summary>
[TestClass]
public sealed class PublishCommandSelectHighestVersionsTests
{
    private static LoadedManifest MacOsManifest(string path, string? architecture)
    {
        var manifest = TestManifests.CreateValid();
        manifest.Apps = [TestManifests.CreateValidMacOsApp(architecture)];
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
}
