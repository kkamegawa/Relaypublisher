using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class ManifestSetValidationTests
{
    private readonly ManifestSetValidator _validator = new();

    [TestMethod]
    public void Validate_DistinctPackages_Passes()
    {
        var errors = _validator.Validate(
        [
            new LoadedManifest("manifests/a.yaml", TestManifests.CreateValid("x64", "Contoso.ToolA", "Tool A [Windows x64]")),
            new LoadedManifest("manifests/b.yaml", TestManifests.CreateValid("x64", "Contoso.ToolB", "Tool B [Windows x64]")),
        ]);

        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public void Validate_DuplicateIdentityAcrossManifests_Fails()
    {
        var errors = _validator.Validate(
        [
            new LoadedManifest("manifests/a.yaml", TestManifests.CreateValid("x64")),
            new LoadedManifest("manifests/b.yaml", TestManifests.CreateValid("x64")),
        ]);

        Assert.IsTrue(
            errors.Any(e => e.Contains("Duplicate app identity", StringComparison.Ordinal)),
            string.Join(" / ", errors));
    }

    [TestMethod]
    public void Validate_SamePackageDifferentVersions_Passes()
    {
        var older = TestManifests.CreateValid("x64");
        older.PackageVersion = "1.0.0";
        var newer = TestManifests.CreateValid("x64");
        newer.PackageVersion = "1.2.3";

        var errors = _validator.Validate(
        [
            new LoadedManifest("manifests/contoso-tool/1.0.0.yaml", older),
            new LoadedManifest("manifests/contoso-tool/1.2.3.yaml", newer),
        ]);

        Assert.IsEmpty(errors, string.Join(" / ", errors));
    }

    [TestMethod]
    public void Validate_DifferentArchitectures_Passes()
    {
        var errors = _validator.Validate(
        [
            new LoadedManifest("manifests/x64.yaml", TestManifests.CreateValid("x64")),
            new LoadedManifest("manifests/arm64.yaml", TestManifests.CreateValid("arm64")),
        ]);

        Assert.IsEmpty(errors, string.Join(" / ", errors));
    }

    [TestMethod]
    public void Validate_DuplicateDisplayNameAcrossPackages_Fails()
    {
        var errors = _validator.Validate(
        [
            new LoadedManifest("manifests/a.yaml", TestManifests.CreateValid("x64", "Contoso.ToolA", "Contoso Tool [Windows x64]")),
            new LoadedManifest("manifests/b.yaml", TestManifests.CreateValid("x64", "Contoso.ToolB", "Contoso Tool [Windows x64]")),
        ]);

        Assert.IsTrue(
            errors.Any(e => e.Contains("Duplicate DisplayName", StringComparison.Ordinal)),
            string.Join(" / ", errors));
    }

    [TestMethod]
    public void Validate_DuplicateIdentityWithinOneManifest_Fails()
    {
        var manifest = TestManifests.CreateValid("x64");
        manifest.Apps.Add(TestManifests.CreateValidApp("x64", "Contoso Tool duplicate [Windows x64]"));

        var errors = _validator.Validate([new LoadedManifest("manifests/a.yaml", manifest)]);

        Assert.IsTrue(
            errors.Any(e => e.Contains("Duplicate app identity", StringComparison.Ordinal)),
            string.Join(" / ", errors));
    }
}
