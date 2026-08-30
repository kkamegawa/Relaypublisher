using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
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
            Entry("Distribution", "<installer-gui-script><bundle id=\"com.contoso.agent\" CFBundleShortVersionString=\"2.0\" CFBundleVersion=\"200\" /></installer-gui-script>", compression: HeapCompression.Gzip),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.agent", result.Bundles[0].BundleId);
        Assert.AreEqual("2.0", result.Bundles[0].BundleVersion);
        Assert.AreEqual("200", result.Bundles[0].BundleBuildVersion);
        Assert.AreEqual("Distribution", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_PackageInfoBzip2_ReturnsBundleFacts()
    {
        // The XAR spec's compression enum is none/gzip/bzip2 (issue #127); real Microsoft-shipped
        // packages (e.g. Global Secure Access Client) use bzip2 for this entry.
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.tool\" CFBundleShortVersionString=\"1.2.3\" CFBundleVersion=\"123\" /></pkg-info>", compression: HeapCompression.Bzip2),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.tool", result.Bundles[0].BundleId);
        Assert.AreEqual("1.2.3", result.Bundles[0].BundleVersion);
        Assert.AreEqual("123", result.Bundles[0].BundleBuildVersion);
        Assert.AreEqual("PackageInfo", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_DistributionBzip2_ReturnsBundleFacts()
    {
        var pkg = BuildXar(
        [
            Entry("Distribution", "<installer-gui-script><bundle id=\"com.contoso.agent\" CFBundleShortVersionString=\"2.0\" CFBundleVersion=\"200\" /></installer-gui-script>", compression: HeapCompression.Bzip2),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(1, result.Bundles.Count);
        Assert.AreEqual("com.contoso.agent", result.Bundles[0].BundleId);
        Assert.AreEqual("Distribution", result.Bundles[0].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_MixedGzipAndBzip2Entries_ReturnsBundleFacts()
    {
        // XAR encodes each heap entry's compression independently, so a PackageInfo/Distribution pair
        // compressed with two different codecs in one archive is a realistic TOC shape, not contrived.
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle CFBundleIdentifier=\"com.contoso.primary\" CFBundleShortVersionString=\"1.0\" /></pkg-info>", compression: HeapCompression.Bzip2),
            Entry("Distribution", "<installer-gui-script><bundle id=\"com.contoso.primary\" CFBundleShortVersionString=\"9.9\" /><bundle id=\"com.contoso.second\" CFBundleShortVersionString=\"2.0\" /></installer-gui-script>", compression: HeapCompression.Gzip),
        ]);

        var result = await InspectAsync(pkg);

        Assert.AreEqual(2, result.Bundles.Count);
        Assert.AreEqual("com.contoso.primary", result.Bundles[0].BundleId);
        Assert.AreEqual("1.0", result.Bundles[0].BundleVersion);
        Assert.AreEqual("PackageInfo", result.Bundles[0].SourceEntry);
        Assert.AreEqual("com.contoso.second", result.Bundles[1].BundleId);
        Assert.AreEqual("Distribution", result.Bundles[1].SourceEntry);
    }

    [TestMethod]
    public async Task Inspect_Bzip2EntryWithTrailingBytes_FailsClosed()
    {
        // Regression guard for the trailing-bytes check generalized from "gzip is not null" to "any
        // decompressor is not null" (issue #127) - without it, appended garbage after a valid bzip2
        // stream would silently pass. BZip2InputStream stops at the first end-of-stream marker (verified
        // against the SharpZipLib source), so the appended junk below is never consumed and must be
        // caught by the "trailing compressed bytes" check, not by the decompressor itself.
        var plaintext = Encoding.UTF8.GetBytes("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>");
        var withTrailingJunk = Bzip2(plaintext).Concat(new byte[] { 0x00, 0x01, 0x02, 0x03 }).ToArray();

        var pkg = BuildXar(
        [
            Entry(
                "PackageInfo", withTrailingJunk, compression: HeapCompression.None,
                encoding: "application/x-bzip2", sizeOverride: (ulong)plaintext.Length),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_Bzip2EntryWithInvalidHeader_FailsClosed()
    {
        // BZip2InputStream's constructor eagerly reads and validates the first block header - unlike
        // GZipStream, construction itself can throw on malformed input (discovered while implementing
        // Bzip2DecompressionStream: this exact case originally escaped as a raw BZip2Exception before
        // the adapter's constructor also translated exceptions, not just Read()).
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", encoding: "application/x-bzip2"),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_CorruptBzip2Entry_FailsClosed()
    {
        // Complements Inspect_Bzip2EntryWithInvalidHeader_FailsClosed: this stream has a valid bzip2
        // header (so BZip2InputStream's constructor succeeds) but is corrupted deeper in, so the failure
        // instead surfaces from Read(). BZip2Exception derives from plain Exception, not IOException, so
        // if Bzip2DecompressionStream's Read() translation is missing, this fails with a raw
        // BZip2Exception instead of the expected PkgInspectionException.
        var valid = Bzip2(Encoding.UTF8.GetBytes("<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>"));
        var corrupt = (byte[])valid.Clone();
        corrupt[^1] ^= 0xFF;
        corrupt[^2] ^= 0xFF;

        var pkg = BuildXar(
        [
            Entry("PackageInfo", corrupt, compression: HeapCompression.None, encoding: "application/x-bzip2"),
        ]);

        await AssertInspectionFailureAsync(pkg);
    }

    [TestMethod]
    public async Task Inspect_Bzip2EntryExceedingMetadataLimit_FailsClosed()
    {
        // A genuine decompression-bomb test: unlike Inspect_MetadataEntryLimitExceeded_FailsClosed
        // (which fails on the declared-size mismatch before the bounded read loop's own size check ever
        // trips), this content actually decompresses past MaxMetadataEntryBytes, exercising the
        // `total > MaxMetadataEntryBytes` branch directly.
        var oversized = Encoding.UTF8.GetBytes(
            "<pkg-info><bundle id=\"com.contoso.app\" />" + new string('x', MaxMetadataEntryBytes + 1) + "</pkg-info>");

        var pkg = BuildXar(
        [
            Entry(
                "PackageInfo", oversized, compression: HeapCompression.Bzip2,
                sizeOverride: (ulong)oversized.Length),
        ]);

        await AssertInspectionFailureAsync(pkg);
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
        // application/x-bzip2 moved from this test to the supported set (issue #127); x-lzma is outside
        // the XAR spec's none/gzip/bzip2 enum and is expected to remain unsupported.
        var pkg = BuildXar(
        [
            Entry("PackageInfo", "<pkg-info><bundle id=\"com.contoso.app\" /></pkg-info>", encoding: "application/x-lzma"),
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

    /// <summary>
    /// The XAR spec defines exactly three heap-entry compression values (doc/00-overview.md,
    /// doc/adr-phase-2.md 2026-08-30, issue #127); this enum mirrors that closed set instead of a second
    /// mutually-exclusive bool, which would allow an illegal "gzip and bzip2 both true" fixture state.
    /// </summary>
    private enum HeapCompression
    {
        None,
        Gzip,
        Bzip2,
    }

    private static byte[] BuildPackageInfo(string xml, HeapCompression compression = HeapCompression.None)
        => BuildXar([Entry("PackageInfo", xml, compression)]);

    private static XarEntrySpec Entry(
        string name,
        string xml,
        HeapCompression compression = HeapCompression.None,
        string? encoding = null,
        string? offsetText = null,
        ulong? sizeOverride = null)
        => Entry(name, Encoding.UTF8.GetBytes(xml), compression, encoding, offsetText, sizeOverride);

    private static XarEntrySpec Entry(
        string name,
        byte[] content,
        HeapCompression compression = HeapCompression.None,
        string? encoding = null,
        string? offsetText = null,
        ulong? sizeOverride = null)
        => new(name, content, compression, encoding, offsetText, sizeOverride);

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
            var physical = entry.Compression switch
            {
                HeapCompression.Gzip => Gzip(entry.Content),
                HeapCompression.Bzip2 => Bzip2(entry.Content),
                _ => entry.Content,
            };
            var offset = heap.Position;
            heap.Write(physical);
            var encoding = entry.Encoding ?? entry.Compression switch
            {
                HeapCompression.Gzip => "application/x-gzip",
                HeapCompression.Bzip2 => "application/x-bzip2",
                _ => "application/octet-stream",
            };
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

    private static byte[] Bzip2(byte[] content)
    {
        // Unlike GZipStream, BZip2OutputStream has no CompressionLevel parameter and no leaveOpen
        // constructor argument - IsStreamOwner (default true) is set false instead.
        using var output = new MemoryStream();
        using (var bzip2 = new BZip2OutputStream(output) { IsStreamOwner = false })
        {
            bzip2.Write(content);
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
        HeapCompression Compression,
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
