using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests.Packaging;

[TestClass]
public sealed class XarPkgBundleInspectorTests
{
    private const int MaxCompressedTocBytes = 16 * 1024 * 1024;
    private const int MaxExpandedTocBytes = 64 * 1024 * 1024;
    private const int MaxMetadataEntryBytes = 16 * 1024 * 1024;

    private static readonly XarPkgBundleInspector Inspector = new();

    [TestMethod]
    public async Task Inspect_PackageInfoUncompressed_ReturnsBundleFacts()
    {
        var pkg = BuildPackageInfo(
            "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.tool\" CFBundleShortVersionString=\"1.2.3\" CFBundleVersion=\"123\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.AreEqual(XarPkgBundleInspector.CurrentInspectorVersion, result.InspectorVersion);
        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.tool", result.Bundles[0].BundleId);
        Assert.AreEqual("1.2.3", result.Bundles[0].BundleVersion);
        Assert.AreEqual("123", result.Bundles[0].BundleBuildVersion);
        Assert.AreEqual("PackageInfo", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_BundleWithAppQualifiedPath_IsAccepted()
    {
        var pkg = BuildPackageInfo(
            "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.tool\" CFBundleShortVersionString=\"1.0\" path=\"Applications/Contoso Tool.app\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.tool", result.Bundles[0].BundleId);
    }

    [TestMethod]
    public async Task Inspect_BundleWithNonAppPath_IsExcluded()
    {
        // A declared path is only ever used to *exclude* a non-application component (a framework or
        // helper nested inside the .app); a bundle with no path at all is still accepted (see the next test).
        var pkg = BuildPackageInfo(
            "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.helper\" CFBundleShortVersionString=\"1.0\" path=\"Applications/Contoso Tool.app/Contents/Frameworks/Helper.framework\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.IsEmpty(result.Bundles);
    }

    [TestMethod]
    public async Task Inspect_BundleWithNoPathAttribute_IsStillAccepted()
    {
        // PackageInfo bundle records commonly omit `path`; treating that as "not an app" would silently
        // drop legitimately detected applications, so absence of a path is not itself exclusionary.
        var pkg = BuildPackageInfo(
            "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.tool\" CFBundleShortVersionString=\"1.0\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.tool", result.Bundles[0].BundleId);
    }

    [TestMethod]
    public async Task Inspect_DistributionGzip_ReturnsBundleFacts()
    {
        var pkg = BuildXar(
        [
            Entry("Distribution", "<installer-gui-script><bundle id=\"com.contoso.agent\" CFBundleShortVersionString=\"2.0\" CFBundleVersion=\"200\" /></installer-gui-script>", gzip: true),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.agent", result.Bundles[0].BundleId);
        Assert.AreEqual("2.0", result.Bundles[0].BundleVersion);
        Assert.AreEqual("200", result.Bundles[0].BundleBuildVersion);
        Assert.AreEqual("Distribution", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_DistributionBundleVersion_ReturnsBundleFacts()
    {
        var pkg = BuildXar(
        [
            Entry("Distribution", "<installer-gui-script><bundle-version id=\"com.contoso.pkg\" CFBundleShortVersionString=\"4.0\" CFBundleVersion=\"400\" /></installer-gui-script>"),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.pkg", result.Bundles[0].BundleId);
        Assert.AreEqual("4.0", result.Bundles[0].BundleVersion);
        Assert.AreEqual("400", result.Bundles[0].BundleBuildVersion);
        Assert.AreEqual("Distribution", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_PackageInfoAndDistribution_DeduplicatesWithPackageInfoPriority()
    {
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.primary\" CFBundleShortVersionString=\"1.0\" CFBundleVersion=\"100\" /><bundle CFBundleIdentifier=\"com.contoso.first\" CFBundleShortVersionString=\"1.1\" CFBundleVersion=\"110\" /></pkg-info>"),
            Entry("Distribution", "<installer-gui-script><bundle id=\"com.contoso.primary\" CFBundleShortVersionString=\"9.9\" CFBundleVersion=\"999\" /><bundle id=\"com.contoso.second\" CFBundleShortVersionString=\"2.0\" CFBundleVersion=\"200\" /></installer-gui-script>"),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(3, result.Bundles.Count);
        Assert.AreEqual("com.contoso.primary", result.Bundles[0].BundleId);
        Assert.AreEqual("1.0", result.Bundles[0].BundleVersion);
        Assert.AreEqual("PackageInfo", result.Bundles[0].SourceEntry);
        Assert.AreEqual("com.contoso.first", result.Bundles[1].BundleId);
        Assert.AreEqual("com.contoso.second", result.Bundles[2].BundleId);
        Assert.AreEqual("Distribution", result.Bundles[2].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_RequiredBundlesAndHelpers_AreNotReportedAsInstalledApplications()
    {
        var pkg = BuildPackageInfo(
            "<pkg-info><required-bundles><bundle id=\"com.contoso.helper\" CFBundleShortVersionString=\"1.0\" CFBundleVersion=\"1\" /><bundle-version id=\"com.contoso.helper2\" version=\"1.0\" build=\"1\" /></required-bundles><bundle CFBundleIdentifier=\"com.contoso.app\" CFBundleShortVersionString=\"3.0\" CFBundleVersion=\"300\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.app", result.Bundles[0].BundleId);
        Assert.AreEqual("300", result.Bundles[0].BundleBuildVersion);
    }

    [TestMethod]
    public async Task Inspect_DuplicateBundleInOneMetadataEntry_DeduplicatesInTocOrder()
    {
        var pkg = BuildPackageInfo(
            "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.app\" CFBundleShortVersionString=\"1.0\" /><bundle CFBundleIdentifier=\"com.contoso.app\" CFBundleShortVersionString=\"9.0\" /></pkg-info>");

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("1.0", result.Bundles[0].BundleVersion);
    }

    [TestMethod]
    public async Task Inspect_InvalidMagic_FailsClosed()
    {
        var pkg = BuildPackageInfo("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>");
        pkg[0] = (byte)'b';

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_UnsupportedHeaderVersion_FailsClosed()
    {
        var pkg = BuildXar([Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>")], version: 2);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_TruncatedHeader_FailsClosed()
    {
        await AssertInspectionFailureAsync(new byte[27]);
    }

    [TestMethod]
    public async Task Inspect_HeaderShorterThanMinimum_FailsClosed()
    {
        var pkg = BuildPackageInfo("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>");
        BinaryPrimitives.WriteUInt16BigEndian(pkg.AsSpan(4, 2), 27);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_HeaderExtendsBeyondArchive_FailsClosed()
    {
        var pkg = BuildPackageInfo("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>");
        BinaryPrimitives.WriteUInt16BigEndian(pkg.AsSpan(4, 2), 64);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_TruncatedCompressedToc_FailsClosed()
    {
        var pkg = BuildXar(
            [Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>")],
            compressedTocLengthOverride: 4096);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_TruncatedExpandedToc_FailsClosed()
    {
        var pkg = BuildXar(
            [Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>")],
            expandedTocLengthOverride: 4096);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_InvalidCompressedToc_FailsClosed()
    {
        var pkg = BuildXar([], rawCompressedToc: "not-zlib"u8.ToArray());

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_ExpandedTocContainsMoreBytesThanDeclared_FailsClosed()
    {
        var pkg = BuildXar(
            [Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>")],
            expandedTocLengthOverride: 1);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_ArchiveEntryOutsideHeap_FailsClosed()
    {
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", offsetText: "999999"),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_UnsignedOffsetOverflowText_FailsClosed()
    {
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", offsetText: "18446744073709551615"),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_UnsupportedHeapEncoding_FailsClosed()
    {
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", encoding: "application/x-bzip2"),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_MalformedTocXml_FailsClosed()
    {
        var pkg = BuildXar([], tocXml: "<xar><toc><file>");

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_TocDtdXml_FailsClosed()
    {
        var toc = "<!DOCTYPE xar [<!ENTITY forbidden \"blocked\">]><xar><toc>&forbidden;</toc></xar>";
        var pkg = BuildXar([], tocXml: toc);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_MalformedMetadataXml_FailsClosed()
    {
        var pkg = BuildPackageInfo("<pkg-info><bundle id=\"com.contoso.app\"></pkg-info>");

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_DtdMetadataXml_FailsClosed()
    {
        var xml = "<!DOCTYPE pkg-info [<!ENTITY forbidden \"blocked\">]><pkg-info><bundle id=\"com.contoso.app\" CFBundleShortVersionString=\"&forbidden;\" /></pkg-info>";
        var pkg = BuildPackageInfo(xml);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_InvalidUtf8MetadataXml_FailsClosed()
    {
        var invalidXml = "<pkg-info><bundle id=\"com.contoso.app\" />"u8.ToArray()
            .Concat(new byte[] { 0xff })
            .Concat("</pkg-info>"u8.ToArray())
            .ToArray();
        var pkg = BuildXar([Entry("PackageInfo", invalidXml)]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_CompressedTocLimitExceeded_FailsBeforeReadingToc()
    {
        var pkg = BuildXar([], compressedTocLengthOverride: (ulong)MaxCompressedTocBytes + 1);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_ExpandedTocLimitExceeded_FailsBeforeAllocatingToc()
    {
        var pkg = BuildXar([], expandedTocLengthOverride: (ulong)MaxExpandedTocBytes + 1);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_MetadataEntryLimitExceeded_FailsClosed()
    {
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", sizeOverride: (ulong)MaxMetadataEntryBytes + 1),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_MetadataXmlDepthLimitExceeded_FailsClosed()
    {
        var nested = new StringBuilder("<pkg-info>");
        for (var i = 0; i < 65; i++)
        {
            nested.Append("<level>");
        }

        nested.Append("<bundle id=\"com.contoso.app\" />");
        for (var i = 0; i < 65; i++)
        {
            nested.Append("</level>");
        }

        nested.Append("</pkg-info>");

        await AssertInspectionFailureAsync(BuildPackageInfo(nested.ToString()));
    }

    [TestMethod]
    public async Task Inspect_MetadataXmlDepthAtLimit_Succeeds()
    {
        var nested = new StringBuilder("<pkg-info>");
        for (var i = 0; i < 63; i++)
        {
            nested.Append("<level>");
        }

        nested.Append("<bundle id=\"com.contoso.app\" />");
        for (var i = 0; i < 63; i++)
        {
            nested.Append("</level>");
        }

        nested.Append("</pkg-info>");
        var result = await InspectAsync(BuildPackageInfo(nested.ToString()));

        Assert.AreEqual("com.contoso.app", result.Bundles[0].BundleId);
    }

    [TestMethod]
    public async Task Inspect_Exactly4096BundleRecords_Succeeds()
    {
        var xml = new StringBuilder("<pkg-info>");
        for (var i = 0; i < 4096; i++)
        {
            xml.Append("<bundle id=\"com.contoso.app").Append(i).Append("\" />");
        }

        xml.Append("</pkg-info>");
        var result = await InspectAsync(BuildPackageInfo(xml.ToString()));

        Assert.AreEqual(4096, result.Bundles.Count);
        Assert.AreEqual("com.contoso.app4095", result.Bundles[^1].BundleId);
    }

    [TestMethod]
    public async Task Inspect_Exactly4096BundlesRepeatedAcrossSources_SucceedsAfterDeduplication()
    {
        var packageInfo = new StringBuilder("<pkg-info>");
        var distribution = new StringBuilder("<installer-gui-script>");
        for (var i = 0; i < 4096; i++)
        {
            packageInfo.Append("<bundle id=\"com.contoso.app").Append(i).Append("\" />");
            distribution.Append("<bundle id=\"com.contoso.app").Append(i).Append("\" />");
        }

        packageInfo.Append("</pkg-info>");
        distribution.Append("</installer-gui-script>");
        var result = await InspectAsync(BuildXar(
        [
            Entry("PackageInfo", packageInfo.ToString()),
            Entry("Distribution", distribution.ToString()),
        ]));

        Assert.AreEqual(4096, result.Bundles.Count);
        Assert.AreEqual("PackageInfo", result.Bundles[^1].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_TooManyBundleRecords_FailsClosed()
    {
        var xml = new StringBuilder("<pkg-info>");
        for (var i = 0; i < 4097; i++)
        {
            xml.Append("<bundle id=\"com.contoso.app").Append(i).Append("\" />");
        }

        xml.Append("</pkg-info>");

        await AssertInspectionFailureAsync(BuildPackageInfo(xml.ToString()));
    }

    [TestMethod]
    public async Task Inspect_CanceledToken_StopsBeforeReadingArchive()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => Inspector.InspectAsync(new MemoryStream([0]), cancellation.Token));

        Assert.IsTrue(exception.CancellationToken.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Inspect_NullStream_ThrowsArgumentNullException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => Inspector.InspectAsync(null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task Inspect_NonSeekableStream_FailsClosed()
    {
        await AssertInspectionFailureAsync(new NonSeekableReadStream(
            BuildPackageInfo("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>")));
    }

    private static async Task<PkgBundleInspectionResult> InspectAsync(byte[] pkg)
    {
        using var stream = new MemoryStream(pkg, writable: false);
        return await Inspector.InspectAsync(stream, CancellationToken.None);
    }

    private static async Task AssertInspectionFailureAsync(byte[] pkg)
    {
        using var stream = new MemoryStream(pkg, writable: false);
        await Assert.ThrowsExactlyAsync<PkgInspectionException>(
            () => Inspector.InspectAsync(stream, CancellationToken.None));
    }

    private static async Task AssertInspectionFailureAsync(Stream pkg)
    {
        await using (pkg)
        {
            await Assert.ThrowsExactlyAsync<PkgInspectionException>(
                () => Inspector.InspectAsync(pkg, CancellationToken.None));
        }
    }

    private static byte[] BuildPackageInfo(string xml, bool gzip = false)
        => BuildXar([Entry("PackageInfo", xml, gzip)]);

    private static XarEntrySpec Entry(
        string name,
        string xml,
        bool gzip = false,
        string? encoding = null,
        string? offsetText = null,
        ulong? sizeOverride = null)
        => Entry(name, Encoding.UTF8.GetBytes(xml), gzip, encoding, offsetText, sizeOverride);

    private static XarEntrySpec Entry(
        string name,
        byte[] content,
        bool gzip = false,
        string? encoding = null,
        string? offsetText = null,
        ulong? sizeOverride = null)
        => new(name, content, gzip, encoding, offsetText, sizeOverride);

    private static byte[] BuildXar(
        IReadOnlyList<XarEntrySpec> entries,
        ushort version = 1,
        ulong? compressedTocLengthOverride = null,
        ulong? expandedTocLengthOverride = null,
        byte[]? rawCompressedToc = null,
        string? tocXml = null)
    {
        var heap = new MemoryStream();
        var tocBuilder = new StringBuilder("<xar><toc>");
        foreach (var entry in entries)
        {
            var physical = entry.Gzip ? Gzip(entry.Content) : entry.Content;
            var offset = heap.Position;
            heap.Write(physical);
            var encoding = entry.Encoding ?? (entry.Gzip ? "application/x-gzip" : "application/octet-stream");
            var size = entry.SizeOverride ?? (ulong)entry.Content.Length;
            var offsetText = entry.OffsetText ?? offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            tocBuilder.Append("<file><name>")
                .Append(entry.Name)
                .Append("</name><type>file</type><data><length>")
                .Append(physical.Length)
                .Append("</length><offset>")
                .Append(offsetText)
                .Append("</offset><size>")
                .Append(size)
                .Append("</size><encoding style=\"")
                .Append(encoding)
                .Append("\" /></data></file>");
        }

        tocBuilder.Append("</toc></xar>");
        var toc = tocXml is null ? Encoding.UTF8.GetBytes(tocBuilder.ToString()) : Encoding.UTF8.GetBytes(tocXml);
        var compressedToc = rawCompressedToc ?? Zlib(toc);
        var declaredCompressedLength = compressedTocLengthOverride ?? (ulong)compressedToc.Length;
        var declaredExpandedLength = expandedTocLengthOverride ?? (ulong)toc.Length;

        using var result = new MemoryStream();
        result.Write("xar!"u8);
        WriteUInt16BigEndian(result, 28);
        WriteUInt16BigEndian(result, version);
        WriteUInt64BigEndian(result, declaredCompressedLength);
        WriteUInt64BigEndian(result, declaredExpandedLength);
        WriteUInt32BigEndian(result, 0);
        result.Write(compressedToc);
        result.Write(heap.ToArray());
        return result.ToArray();
    }

    private static byte[] Zlib(byte[] content)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed record XarEntrySpec(
        string Name,
        byte[] Content,
        bool Gzip,
        string? Encoding,
        string? OffsetText,
        ulong? SizeOverride);

    private sealed class NonSeekableReadStream : MemoryStream
    {
        public NonSeekableReadStream(byte[] content)
            : base(content, writable: false)
        {
        }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }
    }
}
