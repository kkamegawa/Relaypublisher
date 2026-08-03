using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class PackageMetadataReaderTests
{
    private string _packageDirectory = null!;

    [TestInitialize]
    public void Initialize()
        => _packageDirectory = Directory.CreateTempSubdirectory("package-metadata-tests-").FullName;

    [TestCleanup]
    public void Cleanup()
        => Directory.Delete(_packageDirectory, recursive: true);

    private static readonly AppIdentity Identity = new("Contoso.Tool", "windows", "x64");

    private static PackageMetadata CreateMetadata(
        string packageIdentifier = "Contoso.Tool",
        string platform = "windows",
        string architecture = "x64",
        string intuneWinFile = "install.intunewin") => new(
        packageIdentifier,
        "1.2.3",
        platform,
        architecture,
        "hash-1",
        new PackageToolMetadata("IntuneWinAppUtil.exe", "1.8.6", "toolsha"),
        intuneWinFile,
        "packagesha",
        DateTimeOffset.Parse("2026-07-06T00:00:00Z"));

    /// <summary>Writes metadata + .intunewin exactly the way IntuneWinPackager lays them out.</summary>
    private string WriteEntry(PackageMetadata metadata, AppIdentity identity, bool writePackageFile = true)
    {
        var entryDirectory = Path.Combine(
            _packageDirectory, identity.PackageIdentifier, $"{identity.Platform}-{identity.Architecture}");
        Directory.CreateDirectory(entryDirectory);
        File.WriteAllText(
            Path.Combine(entryDirectory, PackageMetadataJson.FileName),
            JsonSerializer.Serialize(metadata, PackageMetadataJson.SerializerOptions));
        if (writePackageFile)
        {
            File.WriteAllBytes(Path.Combine(entryDirectory, metadata.IntuneWinFile!), [1, 2, 3]);
        }

        return entryDirectory;
    }

    [TestMethod]
    public async Task ReadAsync_RoundTripsMetadataWrittenWithSharedOptions()
    {
        var entryDirectory = WriteEntry(CreateMetadata(), Identity);

        var artifacts = await PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None);

        Assert.AreEqual("Contoso.Tool", artifacts.Metadata.PackageIdentifier);
        Assert.AreEqual("1.2.3", artifacts.Metadata.PackageVersion);
        Assert.AreEqual("hash-1", artifacts.Metadata.InputHash);
        Assert.AreEqual("1.8.6", artifacts.Metadata.Tool!.Version);
        Assert.AreEqual(Path.Combine(entryDirectory, "install.intunewin"), artifacts.ContentPath);
    }

    [TestMethod]
    public async Task ReadAsync_NullToolVersion_RoundTripsAsNull()
    {
        var metadata = CreateMetadata() with { Tool = new PackageToolMetadata("IntuneWinAppUtil.exe", null, "toolsha") };
        WriteEntry(metadata, Identity);

        var artifacts = await PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None);

        Assert.IsNull(artifacts.Metadata.Tool!.Version);
    }

    [TestMethod]
    public async Task ReadAsync_MissingMetadataFile_Throws()
    {
        var exception = await Assert.ThrowsExactlyAsync<PackagingException>(
            () => PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Run the package command");
    }

    [TestMethod]
    public async Task ReadAsync_MalformedJson_Throws()
    {
        var entryDirectory = Path.Combine(_packageDirectory, Identity.PackageIdentifier, "windows-x64");
        Directory.CreateDirectory(entryDirectory);
        File.WriteAllText(Path.Combine(entryDirectory, PackageMetadataJson.FileName), "{not json");

        await Assert.ThrowsExactlyAsync<PackagingException>(
            () => PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadAsync_IdentityMismatch_Throws()
    {
        WriteEntry(CreateMetadata(packageIdentifier: "Fabrikam.Other"), Identity);

        var exception = await Assert.ThrowsExactlyAsync<PackagingException>(
            () => PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Fabrikam.Other");
    }

    [TestMethod]
    public async Task ReadAsync_PlatformCasingDiffers_Matches()
    {
        WriteEntry(CreateMetadata(platform: "Windows", architecture: "X64"), Identity);

        var artifacts = await PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None);

        Assert.AreEqual("Windows", artifacts.Metadata.Platform);
    }

    [TestMethod]
    public async Task ReadAsync_TraversalInIntuneWinFile_Throws()
    {
        WriteEntry(CreateMetadata(intuneWinFile: "../escape.intunewin"), Identity, writePackageFile: false);

        await Assert.ThrowsExactlyAsync<UnsafePathException>(
            () => PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadAsync_MissingIntuneWinFile_Throws()
    {
        WriteEntry(CreateMetadata(), Identity, writePackageFile: false);

        var exception = await Assert.ThrowsExactlyAsync<PackagingException>(
            () => PackageMetadataReader.ReadAsync(_packageDirectory, Identity, CancellationToken.None));

        StringAssert.Contains(exception.Message, "Re-run the package command");
    }
}
