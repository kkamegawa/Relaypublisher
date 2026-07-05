using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class ManagementMetadataTests
{
    private static ManagementMetadata CreateSample() => new()
    {
        PackageIdentifier = "Contoso.Tool",
        PackageVersion = "1.2.3",
        Platform = "windows",
        Architecture = "x64",
        ManifestPath = "manifests/Contoso/Contoso.Tool/1.2.3/Contoso.Tool.yaml",
        ManifestHash = "manifest-hash",
        InputHash = "input-hash",
        SourceCommit = "abc123",
    };

    [TestMethod]
    public void Serialize_ThenTryParse_RoundTripsAllFields()
    {
        var original = CreateSample();

        var json = original.Serialize();
        var parsed = ManagementMetadata.TryParse(json, out var metadata);

        Assert.IsTrue(parsed);
        Assert.AreEqual(original.PackageIdentifier, metadata!.PackageIdentifier);
        Assert.AreEqual(original.PackageVersion, metadata.PackageVersion);
        Assert.AreEqual(original.Platform, metadata.Platform);
        Assert.AreEqual(original.Architecture, metadata.Architecture);
        Assert.AreEqual(original.ManifestPath, metadata.ManifestPath);
        Assert.AreEqual(original.ManifestHash, metadata.ManifestHash);
        Assert.AreEqual(original.InputHash, metadata.InputHash);
        Assert.AreEqual(original.SourceCommit, metadata.SourceCommit);
        Assert.AreEqual(ManagementMetadata.ManagedByValue, metadata.ManagedBy);
    }

    [TestMethod]
    public void Serialize_JsonExceedsNotesLimit_ThrowsManagementMetadataTooLargeException()
    {
        var oversized = new ManagementMetadata
        {
            PackageIdentifier = "Contoso.Tool",
            PackageVersion = "1.2.3",
            Platform = "windows",
            Architecture = "x64",
            ManifestPath = new string('a', ManagementMetadata.NotesMaxLength),
            ManifestHash = "manifest-hash",
            InputHash = "input-hash",
            SourceCommit = "abc123",
        };

        Assert.ThrowsExactly<ManagementMetadataTooLargeException>(() => oversized.Serialize());
    }

    [TestMethod]
    public void TryParse_NullOrBlank_ReturnsFalse()
    {
        Assert.IsFalse(ManagementMetadata.TryParse(null, out var metadata1));
        Assert.IsNull(metadata1);
        Assert.IsFalse(ManagementMetadata.TryParse("   ", out var metadata2));
        Assert.IsNull(metadata2);
    }

    [TestMethod]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        var parsed = ManagementMetadata.TryParse("not json at all", out var metadata);

        Assert.IsFalse(parsed);
        Assert.IsNull(metadata);
    }

    [TestMethod]
    public void TryParse_UnrelatedAdminNotes_ReturnsFalse()
    {
        var parsed = ManagementMetadata.TryParse("Approved by helpdesk on 2026-01-01", out var metadata);

        Assert.IsFalse(parsed);
        Assert.IsNull(metadata);
    }

    [TestMethod]
    public void TryParse_JsonMissingManagedByMarker_ReturnsFalse()
    {
        var json = """{"packageIdentifier":"Contoso.Tool","packageVersion":"1.0.0","platform":"windows","architecture":"x64","manifestPath":"m","manifestHash":"h","inputHash":"i","sourceCommit":"c"}""";

        var parsed = ManagementMetadata.TryParse(json, out var metadata);

        Assert.IsFalse(parsed);
        Assert.IsNull(metadata);
    }

    [TestMethod]
    public void TryParse_JsonWithWrongManagedByMarker_ReturnsFalse()
    {
        var json = """{"managedBy":"some-other-tool","packageIdentifier":"Contoso.Tool","packageVersion":"1.0.0","platform":"windows","architecture":"x64","manifestPath":"m","manifestHash":"h","inputHash":"i","sourceCommit":"c"}""";

        var parsed = ManagementMetadata.TryParse(json, out var metadata);

        Assert.IsFalse(parsed);
        Assert.IsNull(metadata);
    }

    [TestMethod]
    public void TryParse_JsonMissingRequiredField_ReturnsFalse()
    {
        var json = """{"managedBy":"intune-lob-manifest","packageIdentifier":"Contoso.Tool"}""";

        var parsed = ManagementMetadata.TryParse(json, out var metadata);

        Assert.IsFalse(parsed);
        Assert.IsNull(metadata);
    }
}
