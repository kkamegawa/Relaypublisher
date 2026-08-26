using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Inspects the metadata portion of an XAR based macOS PKG.
///
/// The inspector intentionally never follows the archive's payload entries. It reads only the
/// bounded Distribution and PackageInfo XML entries, which keeps inspection useful on a build runner
/// without requiring a macOS <c>pkgutil</c> installation.
/// </summary>
public class XarPkgBundleInspector : IPkgBundleInspector
{
    // This is an inspector contract version, not the product/CLI version. It is persisted with the
    // package report so a future parser can reject a report produced by an incompatible implementation.
    public const string CurrentInspectorVersion = "1";

    private const int XarHeaderLength = 28;
    private const int MaxCompressedTocBytes = 16 * 1024 * 1024;
    private const int MaxExpandedTocBytes = 64 * 1024 * 1024;
    private const int MaxMetadataEntryBytes = 16 * 1024 * 1024;
    private const int MaxBundleRecords = 4_096;
    private const int MaxXmlDepth = 64;
    private const int MaxXmlNodes = 1_000_000;
    private const int MaxXmlLeafCharacters = 1 * 1024 * 1024;

    /// <inheritdoc />
    public async Task<PkgBundleInspectionResult> InspectAsync(
        Stream pkg,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pkg);
        cancellationToken.ThrowIfCancellationRequested();

        if (!pkg.CanRead || !pkg.CanSeek)
        {
            throw new PkgInspectionException("The PKG stream must be readable and seekable for bounded XAR inspection.");
        }

        long archiveLength;
        try
        {
            archiveLength = pkg.Length;
            pkg.Position = 0;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new PkgInspectionException("The PKG stream length or position could not be read.", ex);
        }

        var header = await ReadHeaderAsync(pkg, archiveLength, cancellationToken).ConfigureAwait(false);
        var tocBytes = await ReadTocAsync(pkg, header, cancellationToken).ConfigureAwait(false);
        var entries = await ParseTocAsync(tocBytes, header.HeapStart, archiveLength, cancellationToken)
            .ConfigureAwait(false);

        var packageInfo = entries.Where(entry => entry.Name == "PackageInfo").ToArray();
        var distribution = entries.Where(entry => entry.Name == "Distribution").ToArray();
        if (packageInfo.Length == 0 && distribution.Length == 0)
        {
            throw new PkgInspectionException("The XAR TOC contains neither a Distribution nor a PackageInfo metadata entry.");
        }

        var packageInfoBundles = await ReadMetadataBundlesAsync(pkg, packageInfo, "PackageInfo", cancellationToken)
            .ConfigureAwait(false);
        var distributionBundles = await ReadMetadataBundlesAsync(pkg, distribution, "Distribution", cancellationToken)
            .ConfigureAwait(false);
        // PackageInfo is the authoritative source when it declares a bundle. Distribution fills gaps
        // that occur in older or unusual packages. Repeated component metadata is de-duplicated in TOC
        // order within each source, then PackageInfo wins when both sources declare the same bundle.
        var bundles = MergeBundles(packageInfoBundles, distributionBundles);
        if (bundles.Count > MaxBundleRecords)
        {
            throw new PkgInspectionException($"The PKG declares more than the {MaxBundleRecords} bundle record limit.");
        }

        return new PkgBundleInspectionResult(CurrentInspectorVersion, bundles);
    }

    private static async Task<XarHeader> ReadHeaderAsync(
        Stream stream,
        long archiveLength,
        CancellationToken cancellationToken)
    {
        if (archiveLength < XarHeaderLength)
        {
            throw new PkgInspectionException("The XAR header is truncated.");
        }

        var headerBytes = new byte[XarHeaderLength];
        await ReadExactlyAsync(stream, headerBytes, cancellationToken, "The XAR header is truncated.")
            .ConfigureAwait(false);

        if (!headerBytes.AsSpan(0, 4).SequenceEqual("xar!"u8))
        {
            throw new PkgInspectionException("The PKG is not an XAR archive (missing the xar! magic).");
        }

        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(4, 2));
        var version = BinaryPrimitives.ReadUInt16BigEndian(headerBytes.AsSpan(6, 2));
        if (headerLength < XarHeaderLength)
        {
            throw new PkgInspectionException($"The XAR header length {headerLength} is smaller than the required {XarHeaderLength} bytes.");
        }

        if (version != 1)
        {
            throw new PkgInspectionException($"The XAR version {version} is not supported.");
        }

        if (headerLength > archiveLength)
        {
            throw new PkgInspectionException("The XAR header extends beyond the archive.");
        }

        var compressedTocLength = ReadBoundedLength(
            BinaryPrimitives.ReadUInt64BigEndian(headerBytes.AsSpan(8, 8)),
            MaxCompressedTocBytes,
            "compressed TOC");
        var expandedTocLength = ReadBoundedLength(
            BinaryPrimitives.ReadUInt64BigEndian(headerBytes.AsSpan(16, 8)),
            MaxExpandedTocBytes,
            "expanded TOC");
        if (compressedTocLength == 0 || expandedTocLength == 0)
        {
            throw new PkgInspectionException("The XAR TOC length must be greater than zero.");
        }

        var heapStart = CheckedAdd(headerLength, compressedTocLength, "the XAR heap offset");
        if (heapStart > archiveLength)
        {
            throw new PkgInspectionException("The XAR compressed TOC extends beyond the archive.");
        }

        // The checksum algorithm is intentionally not interpreted here. XAR metadata inspection is
        // bounded by the declared archive ranges; the source SHA256 check is the package integrity gate.
        return new XarHeader(headerLength, compressedTocLength, expandedTocLength, heapStart);
    }

    private static async Task<byte[]> ReadTocAsync(
        Stream stream,
        XarHeader header,
        CancellationToken cancellationToken)
    {
        stream.Position = header.HeaderLength;
        var compressed = new byte[header.CompressedTocLength];
        await ReadExactlyAsync(stream, compressed, cancellationToken, "The compressed XAR TOC is truncated.")
            .ConfigureAwait(false);

        var expanded = new byte[header.ExpandedTocLength];
        try
        {
            using var compressedStream = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
            await ReadExactlyAsync(zlib, expanded, cancellationToken, "The decompressed XAR TOC is shorter than declared.")
                .ConfigureAwait(false);

            // Reading one more byte both detects a TOC that expands beyond its declared size and makes
            // ZLibStream validate its checksum/trailer rather than accepting a prefix of corrupt data.
            var extra = new byte[1];
            var extraCount = await zlib.ReadAsync(extra.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (extraCount != 0)
            {
                throw new PkgInspectionException("The decompressed XAR TOC exceeds its declared size.");
            }
        }
        catch (PkgInspectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            throw new PkgInspectionException("The XAR compressed TOC could not be decompressed.", ex);
        }

        return expanded;
    }

    private static async Task<IReadOnlyList<XarEntry>> ParseTocAsync(
        byte[] tocBytes,
        long heapStart,
        long archiveLength,
        CancellationToken cancellationToken)
    {
        var entries = new List<XarEntry>();
        var frames = new Stack<TocFileFrame>();
        TocDataContext? dataContext = null;
        var nodeCount = 0;

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxExpandedTocBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = true,
        };

        try
        {
            using var tocStream = new MemoryStream(tocBytes, writable: false);
            using var reader = XmlReader.Create(tocStream, settings);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > MaxXmlDepth)
                {
                    throw new PkgInspectionException($"The XAR TOC XML exceeds the maximum depth of {MaxXmlDepth}.");
                }

                if (++nodeCount > MaxXmlNodes)
                {
                    throw new PkgInspectionException($"The XAR TOC XML exceeds the maximum node count of {MaxXmlNodes}.");
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    var name = reader.LocalName;
                    if (name == "file")
                    {
                        if (dataContext is not null)
                        {
                            throw new PkgInspectionException("The XAR TOC contains a file element inside a data element.");
                        }

                        var frame = new TocFileFrame(reader.Depth);
                        frames.Push(frame);
                        if (reader.IsEmptyElement)
                        {
                            frames.Pop();
                            CompleteFileFrame(entries, frame, heapStart, archiveLength);
                        }

                        continue;
                    }

                    if (frames.Count == 0)
                    {
                        continue;
                    }

                    var frameAtElement = frames.Peek();
                    if (reader.Depth == frameAtElement.Depth + 1 && name == "name")
                    {
                        frameAtElement.Name = await ReadLeafAsync(reader, "TOC file name", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (reader.Depth == frameAtElement.Depth + 1 && name == "type")
                    {
                        frameAtElement.Type = await ReadLeafAsync(reader, "TOC file type", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (reader.Depth == frameAtElement.Depth + 1 && name == "data")
                    {
                        if (dataContext is not null)
                        {
                            throw new PkgInspectionException("The XAR TOC contains nested data elements.");
                        }

                        dataContext = new TocDataContext(frameAtElement, reader.Depth);
                        if (reader.IsEmptyElement)
                        {
                            frameAtElement.Data = dataContext.ToData();
                            dataContext = null;
                        }

                        continue;
                    }

                    if (dataContext is not null && reader.Depth == dataContext.Depth + 1)
                    {
                        switch (name)
                        {
                            case "length":
                                dataContext.LengthText = await ReadLeafAsync(reader, "TOC data length", cancellationToken)
                                    .ConfigureAwait(false);
                                break;
                            case "offset":
                                dataContext.OffsetText = await ReadLeafAsync(reader, "TOC data offset", cancellationToken)
                                    .ConfigureAwait(false);
                                break;
                            case "size":
                                dataContext.SizeText = await ReadLeafAsync(reader, "TOC data size", cancellationToken)
                                    .ConfigureAwait(false);
                                break;
                            case "encoding":
                                dataContext.Encoding = reader.GetAttribute("style");
                                if (!reader.IsEmptyElement)
                                {
                                    _ = await ReadLeafAsync(reader, "TOC data encoding", cancellationToken).ConfigureAwait(false);
                                }

                                break;
                        }
                    }

                    continue;
                }

                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (dataContext is not null && reader.Depth == dataContext.Depth)
                    {
                        if (reader.LocalName != "data")
                        {
                            throw new PkgInspectionException("The XAR TOC data element is malformed.");
                        }

                        dataContext.Owner.Data = dataContext.ToData();
                        dataContext = null;
                        continue;
                    }

                    if (reader.LocalName == "file")
                    {
                        if (frames.Count == 0)
                        {
                            throw new PkgInspectionException("The XAR TOC contains an unmatched file end element.");
                        }

                        var frame = frames.Pop();
                        CompleteFileFrame(entries, frame, heapStart, archiveLength);
                    }
                }
            }

            if (frames.Count != 0 || dataContext is not null)
            {
                throw new PkgInspectionException("The XAR TOC ended before all file metadata was closed.");
            }
        }
        catch (PkgInspectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            throw new PkgInspectionException("The XAR TOC XML is invalid or unsafe.", ex);
        }

        return entries;
    }

    private static async Task<string> ReadLeafAsync(
        XmlReader reader,
        string description,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        var elementDepth = reader.Depth;
        var value = new System.Text.StringBuilder();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                value.Append(reader.Value);
                if (value.Length > MaxXmlLeafCharacters)
                {
                    throw new PkgInspectionException($"The {description} exceeds the maximum XML leaf size.");
                }

                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == elementDepth)
            {
                return value.ToString().Trim();
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                throw new PkgInspectionException($"The {description} must not contain nested XML elements.");
            }
        }

        throw new PkgInspectionException($"The {description} XML element is truncated.");
    }

    private static void CompleteFileFrame(
        List<XarEntry> entries,
        TocFileFrame frame,
        long heapStart,
        long archiveLength)
    {
        if (frame.Name is not ("Distribution" or "PackageInfo"))
        {
            return;
        }

        if (!string.Equals(frame.Type, "file", StringComparison.Ordinal))
        {
            throw new PkgInspectionException($"The XAR metadata entry '{frame.Name}' is not a file.");
        }

        if (frame.Data is null)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{frame.Name}' has no data descriptor.");
        }

        var data = frame.Data;
        ValidateArchiveRange(data.Offset, data.Length, heapStart, archiveLength, frame.Name);
        if (data.Length > MaxMetadataEntryBytes)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{frame.Name}' exceeds the {MaxMetadataEntryBytes} byte limit.");
        }

        if (data.UncompressedLength is > MaxMetadataEntryBytes)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{frame.Name}' exceeds the {MaxMetadataEntryBytes} byte limit after decompression.");
        }

        entries.Add(new XarEntry(frame.Name, data.Offset, data.Length, data.UncompressedLength, data.Encoding)
        {
            HeapStart = heapStart,
        });
    }

    private static void ValidateArchiveRange(
        long offset,
        long length,
        long heapStart,
        long archiveLength,
        string entryName)
    {
        if (offset < 0 || length < 0)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{entryName}' has a negative offset or length.");
        }

        var heapLength = archiveLength - heapStart;
        if (heapLength < 0 || offset > heapLength || length > heapLength - offset)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{entryName}' points outside the archive heap.");
        }
    }

    private static async Task<IReadOnlyList<PkgBundleIdentity>> ReadMetadataBundlesAsync(
        Stream archive,
        IReadOnlyList<XarEntry> entries,
        string sourceEntry,
        CancellationToken cancellationToken)
    {
        var result = new List<PkgBundleIdentity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var xmlBytes = await ReadHeapEntryAsync(archive, entry, cancellationToken).ConfigureAwait(false);
            var nodeCount = 0;
            var requiredBundlesDepths = new Stack<int>();
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataEntryBytes,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CloseInput = true,
            };

            try
            {
                using var xmlStream = new MemoryStream(xmlBytes, writable: false);
                using var reader = XmlReader.Create(xmlStream, settings);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.Depth > MaxXmlDepth)
                    {
                        throw new PkgInspectionException($"The {sourceEntry} XML exceeds the maximum depth of {MaxXmlDepth}.");
                    }

                    if (++nodeCount > MaxXmlNodes)
                    {
                        throw new PkgInspectionException($"The {sourceEntry} XML exceeds the maximum node count of {MaxXmlNodes}.");
                    }

                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        if (reader.LocalName == "required-bundles")
                        {
                            if (!reader.IsEmptyElement)
                            {
                                requiredBundlesDepths.Push(reader.Depth);
                            }

                            continue;
                        }

                        var isBundleElement = reader.LocalName == "bundle"
                            || (sourceEntry == "Distribution" && reader.LocalName == "bundle-version");
                        if (!isBundleElement || requiredBundlesDepths.Count != 0)
                        {
                            continue;
                        }

                        // A declared path is only ever used to exclude a component (a framework or
                        // helper nested inside the .app, or any other non-.app resource); no path at
                        // all is common for PackageInfo bundle records and does not, by itself, mean
                        // the bundle is not an installed application.
                        var path = reader.GetAttribute("path");
                        if (path is not null && !IsApplicationBundlePath(path))
                        {
                            continue;
                        }

                        var bundleId = FirstNonBlank(
                            reader.GetAttribute("CFBundleIdentifier"),
                            reader.GetAttribute("id"),
                            reader.GetAttribute("bundle-id"),
                            reader.GetAttribute("identifier"));
                        if (bundleId is null)
                        {
                            throw new PkgInspectionException($"A {sourceEntry} bundle entry has no bundle identifier.");
                        }

                        var bundleVersion = FirstNonBlank(
                            reader.GetAttribute("CFBundleShortVersionString"),
                            reader.GetAttribute("bundle-version"),
                            reader.GetAttribute("version"));
                        var bundleBuildVersion = FirstNonBlank(
                            reader.GetAttribute("CFBundleVersion"),
                            reader.GetAttribute("build-version"),
                            reader.GetAttribute("build"));

                        // Components commonly repeat the same bundle in both metadata files. Keep the
                        // first record in TOC order; PackageInfo is processed before Distribution by the
                        // caller, so that source remains the deterministic authority on conflicts.
                        if (seen.Add(bundleId))
                        {
                            if (result.Count >= MaxBundleRecords)
                            {
                                throw new PkgInspectionException($"The PKG declares more than the {MaxBundleRecords} bundle record limit.");
                            }

                            result.Add(new PkgBundleIdentity(bundleId, bundleVersion, bundleBuildVersion, sourceEntry));
                        }
                    }

                    if (reader.NodeType == XmlNodeType.EndElement &&
                        reader.LocalName == "required-bundles" &&
                        requiredBundlesDepths.Count != 0 &&
                        requiredBundlesDepths.Peek() == reader.Depth)
                    {
                        requiredBundlesDepths.Pop();
                    }
                }
            }
            catch (PkgInspectionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
            {
                throw new PkgInspectionException($"The {sourceEntry} XML is invalid or unsafe.", ex);
            }
        }

        return result;
    }

    private static bool IsApplicationBundlePath(string path)
    {
        var normalized = path.Trim().TrimEnd('/', '\\');
        return normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadHeapEntryAsync(
        Stream archive,
        XarEntry entry,
        CancellationToken cancellationToken)
    {
        var absoluteOffset = CheckedAdd(entry.HeapStart, entry.Offset, $"the {entry.Name} heap offset");
        archive.Position = absoluteOffset;

        using var bounded = new BoundedReadStream(archive, entry.Length);
        Stream input = bounded;
        GZipStream? gzip = null;
        try
        {
            if (entry.Encoding is null || entry.Encoding.Length == 0 || entry.Encoding == "application/octet-stream")
            {
                // The bounded stream is already the input.
            }
            else if (entry.Encoding == "application/x-gzip")
            {
                gzip = new GZipStream(bounded, CompressionMode.Decompress, leaveOpen: true);
                input = gzip;
            }
            else
            {
                throw new PkgInspectionException($"The XAR metadata entry '{entry.Name}' uses unsupported compression '{entry.Encoding}'.");
            }

            var initialCapacity = entry.UncompressedLength is > 0 and <= MaxMetadataEntryBytes
                ? (int)entry.UncompressedLength.Value
                : Math.Min((int)entry.Length, MaxMetadataEntryBytes);
            using var output = new MemoryStream(initialCapacity);
            var buffer = new byte[64 * 1024];
            var total = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total = checked(total + count);
                if (total > MaxMetadataEntryBytes)
                {
                    throw new PkgInspectionException($"The XAR metadata entry '{entry.Name}' exceeds the {MaxMetadataEntryBytes} byte limit after decompression.");
                }

                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            if (entry.UncompressedLength is not null && total != entry.UncompressedLength.Value)
            {
                throw new PkgInspectionException($"The XAR metadata entry '{entry.Name}' length does not match its declared uncompressed size.");
            }

            if (gzip is not null && bounded.Remaining != 0)
            {
                throw new PkgInspectionException($"The XAR metadata entry '{entry.Name}' contains trailing compressed bytes.");
            }

            return output.ToArray();
        }
        catch (PkgInspectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException)
        {
            throw new PkgInspectionException($"The XAR metadata entry '{entry.Name}' could not be read.", ex);
        }
        finally
        {
            gzip?.Dispose();
        }
    }

    private static IReadOnlyList<PkgBundleIdentity> MergeBundles(
        IReadOnlyList<PkgBundleIdentity> packageInfo,
        IReadOnlyList<PkgBundleIdentity> distribution)
    {
        var result = new List<PkgBundleIdentity>(packageInfo.Count + distribution.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bundle in packageInfo)
        {
            seen.Add(bundle.BundleId);
            result.Add(bundle);
        }

        foreach (var bundle in distribution)
        {
            if (seen.Add(bundle.BundleId))
            {
                result.Add(bundle);
            }
        }

        return result;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static int ReadBoundedLength(ulong value, int maximum, string description)
    {
        if (value > (ulong)maximum)
        {
            throw new PkgInspectionException($"The XAR {description} exceeds the {maximum} byte limit.");
        }

        return checked((int)value);
    }

    private static long CheckedAdd(long left, long right, string description)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException ex)
        {
            throw new PkgInspectionException($"The XAR {description} overflows a 64-bit offset.", ex);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken,
        string truncatedMessage)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var count = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new PkgInspectionException(truncatedMessage);
            }

            total += count;
        }
    }

    private sealed record XarHeader(
        int HeaderLength,
        int CompressedTocLength,
        int ExpandedTocLength,
        long HeapStart);

    private sealed record XarEntry(
        string Name,
        long Offset,
        long Length,
        long? UncompressedLength,
        string? Encoding)
    {
        // Set once the TOC has been checked. Keeping this derived value on the entry avoids repeating
        // archive arithmetic when the metadata body is read.
        public long HeapStart { get; init; }
    }

    private sealed class TocFileFrame
    {
        public TocFileFrame(int depth)
        {
            Depth = depth;
        }

        public int Depth { get; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public TocData? Data { get; set; }
    }

    private sealed class TocDataContext
    {
        public TocDataContext(TocFileFrame owner, int depth)
        {
            Owner = owner;
            Depth = depth;
        }

        public TocFileFrame Owner { get; }

        public int Depth { get; }

        public string? LengthText { get; set; }

        public string? OffsetText { get; set; }

        public string? SizeText { get; set; }

        public string? Encoding { get; set; }

        public TocData ToData()
        {
            return new TocData(
                ParseUnsignedLong(OffsetText, "offset"),
                ParseUnsignedLong(LengthText, "length"),
                SizeText is null ? null : ParseUnsignedLong(SizeText, "size"),
                Encoding);
        }
    }

    private sealed record TocData(long Offset, long Length, long? UncompressedLength, string? Encoding);

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public long Remaining => _remaining;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_remaining == 0)
            {
                return 0;
            }

            var requested = (int)Math.Min(buffer.Length, _remaining);
            var count = _inner.Read(buffer[..requested]);
            if (count == 0)
            {
                throw new EndOfStreamException("The XAR heap entry is truncated.");
            }

            _remaining -= count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_remaining == 0)
            {
                return 0;
            }

            var requested = (int)Math.Min(buffer.Length, _remaining);
            var count = await _inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The XAR heap entry is truncated.");
            }

            _remaining -= count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static long ParseUnsignedLong(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value > long.MaxValue)
        {
            throw new PkgInspectionException($"The XAR TOC data {field} is missing or is not a valid non-negative 64-bit integer.");
        }

        return (long)value;
    }
}

/// <summary>Short name retained for dependency injection registrations and tests.</summary>
public sealed class PkgBundleInspector : XarPkgBundleInspector
{
}
