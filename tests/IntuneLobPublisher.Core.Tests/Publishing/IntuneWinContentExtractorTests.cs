using System.IO.Compression;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class IntuneWinContentExtractorTests
{
    private DirectoryInfo _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = Directory.CreateTempSubdirectory("intunewin-extractor-tests-");

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private static string ValidDetectionXml(string fileName = "IntunePackage.intunewin") => $"""
        <ApplicationInfo ToolVersion="1.8.5.0">
          <Name>install.ps1</Name>
          <UnencryptedContentSize>42</UnencryptedContentSize>
          <FileName>{fileName}</FileName>
          <SetupFile>install.ps1</SetupFile>
          <EncryptionInfo>
            <EncryptionKey>{Convert.ToBase64String([1, 2, 3])}</EncryptionKey>
            <MacKey>{Convert.ToBase64String(new byte[32])}</MacKey>
            <InitializationVector>{Convert.ToBase64String(new byte[16])}</InitializationVector>
            <Mac>{Convert.ToBase64String(new byte[32])}</Mac>
            <ProfileIdentifier>ProfileVersion1</ProfileIdentifier>
            <FileDigest>{Convert.ToBase64String([4, 5, 6])}</FileDigest>
            <FileDigestAlgorithm>SHA256</FileDigestAlgorithm>
          </EncryptionInfo>
        </ApplicationInfo>
        """;

    private string CreateIntuneWinFile(string? detectionXml, string? contentEntryName = "IntuneWinPackage/Contents/IntunePackage.intunewin", byte[]? contentBytes = null)
    {
        var path = Path.Combine(_workspace.FullName, $"{Guid.NewGuid()}.intunewin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        if (detectionXml is not null)
        {
            var metadataEntry = archive.CreateEntry("IntuneWinPackage/Metadata/Detection.xml");
            using var writer = new StreamWriter(metadataEntry.Open());
            writer.Write(detectionXml);
        }

        if (contentEntryName is not null)
        {
            var contentEntry = archive.CreateEntry(contentEntryName);
            using var stream = contentEntry.Open();
            stream.Write(contentBytes ?? [1, 2, 3, 4]);
        }

        return path;
    }

    [TestMethod]
    public void Extract_ValidPackage_ParsesEncryptionInfoAndContent()
    {
        var path = CreateIntuneWinFile(ValidDetectionXml());
        var extractor = new IntuneWinContentExtractor();

        using var content = extractor.Extract(path);

        Assert.AreEqual(42, content.UnencryptedContentSize);
        Assert.AreEqual("IntunePackage.intunewin", content.ContentFileName);
        Assert.AreEqual("ProfileVersion1", content.EncryptionInfo.ProfileIdentifier);
        Assert.AreEqual("SHA256", content.EncryptionInfo.FileDigestAlgorithm);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, content.EncryptionInfo.EncryptionKey);
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, content.EncryptionInfo.FileDigest);
        CollectionAssert.AreEqual(new byte[16], content.EncryptionInfo.InitializationVector);

        using var stream = content.OpenEncryptedContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, buffer.ToArray());
    }

    [TestMethod]
    public void Extract_MissingDetectionXml_ThrowsPackagingException()
    {
        var path = CreateIntuneWinFile(detectionXml: null);
        var extractor = new IntuneWinContentExtractor();

        Assert.ThrowsExactly<PackagingException>(() => extractor.Extract(path));
    }

    [TestMethod]
    public void Extract_MissingContentEntry_ThrowsPackagingException()
    {
        var path = CreateIntuneWinFile(ValidDetectionXml(), contentEntryName: null);
        var extractor = new IntuneWinContentExtractor();

        Assert.ThrowsExactly<PackagingException>(() => extractor.Extract(path));
    }

    [TestMethod]
    public void Extract_ContentFileNameWithPathSegment_ThrowsUnsafePathException()
    {
        var path = CreateIntuneWinFile(ValidDetectionXml("../evil.exe"), contentEntryName: "IntuneWinPackage/Contents/evil.exe");
        var extractor = new IntuneWinContentExtractor();

        Assert.ThrowsExactly<UnsafePathException>(() => extractor.Extract(path));
    }

    [TestMethod]
    public void Extract_NonBase64EncryptionKey_ThrowsPackagingException()
    {
        var invalidXml = ValidDetectionXml().Replace(Convert.ToBase64String([1, 2, 3]), "not-base64!!");
        var path = CreateIntuneWinFile(invalidXml);
        var extractor = new IntuneWinContentExtractor();

        Assert.ThrowsExactly<PackagingException>(() => extractor.Extract(path));
    }
}
