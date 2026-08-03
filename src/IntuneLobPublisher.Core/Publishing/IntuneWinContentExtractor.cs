using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The parsed content of a <c>.intunewin</c> package: the encryption info needed to commit the file
/// to Graph, and a stream over the still-encrypted payload to upload to Azure Storage. Owns the
/// underlying <see cref="ZipArchive"/>, so callers must dispose this once the content has been read.
/// </summary>
public sealed class IntuneWinContent : IUploadableContent
{
    private readonly ZipArchive _archive;
    private readonly ZipArchiveEntry _contentEntry;

    public IntuneWinContent(
        ZipArchive archive, ZipArchiveEntry contentEntry, string contentFileName, ContentEncryptionInfo encryptionInfo, long unencryptedContentSize)
    {
        _archive = archive;
        _contentEntry = contentEntry;
        ContentFileName = contentFileName;
        EncryptionInfo = encryptionInfo;
        UnencryptedContentSize = unencryptedContentSize;
    }

    /// <summary>The content file name recorded in Detection.xml (conventionally "IntunePackage.intunewin"), used as the Graph <c>mobileAppContentFile.name</c>.</summary>
    public string ContentFileName { get; }

    public ContentEncryptionInfo EncryptionInfo { get; }

    /// <summary>Original (unencrypted) size in bytes, as recorded by the Content Prep Tool.</summary>
    public long UnencryptedContentSize { get; }

    /// <summary>Size of the still-encrypted payload in bytes, as reported by the .intunewin ZIP entry.</summary>
    public long EncryptedContentSize => _contentEntry.Length;

    /// <summary>Opens the encrypted payload for reading. Callers should not call this more than once per instance.</summary>
    public Stream OpenEncryptedContentStream() => _contentEntry.Open();

    public void Dispose() => _archive.Dispose();
}

/// <summary>
/// Parses the <c>.intunewin</c> ZIP container produced by IntuneWinAppUtil. The container layout and
/// <c>Detection.xml</c> schema are not publicly documented by Microsoft (the tool itself is closed-source),
/// so this follows the widely-used community-reverse-engineered format also referenced by
/// doc/issues/issue-003-intune-graph-win32.md ("the encrypted payload (IntunePackage.intunewin)").
/// </summary>
public sealed class IntuneWinContentExtractor : IUploadableContentExtractor
{
    private const string MetadataEntryName = "IntuneWinPackage/Metadata/Detection.xml";
    private const string ContentDirectory = "IntuneWinPackage/Contents/";

    /// <summary>
    /// Opens a <c>.intunewin</c> file and parses its <c>Detection.xml</c>. The returned
    /// <see cref="IntuneWinContent"/> keeps the ZIP open; dispose it once the content stream has been read.
    /// </summary>
    public IUploadableContent Extract(string intuneWinPath)
    {
        var archive = ZipFile.OpenRead(intuneWinPath);
        try
        {
            var metadataEntry = FindEntry(archive, MetadataEntryName)
                ?? throw new PackagingException($"'{intuneWinPath}' does not contain '{MetadataEntryName}'.");

            XElement applicationInfo;
            using (var metadataStream = metadataEntry.Open())
            {
                applicationInfo = XDocument.Load(metadataStream).Root
                    ?? throw new PackagingException($"'{intuneWinPath}': Detection.xml has no root element.");
            }

            var encryptionInfoElement = applicationInfo.Element("EncryptionInfo")
                ?? throw new PackagingException($"'{intuneWinPath}': Detection.xml is missing 'EncryptionInfo'.");

            var contentFileName = RequireElementValue(applicationInfo, "FileName", intuneWinPath);
            if (contentFileName.Contains('/') || contentFileName.Contains('\\') || contentFileName.Contains(".."))
            {
                throw new UnsafePathException(
                    $"'{intuneWinPath}': Detection.xml FileName '{contentFileName}' is not a plain file name.");
            }

            var contentEntry = FindEntry(archive, ContentDirectory + contentFileName)
                ?? throw new PackagingException(
                    $"'{intuneWinPath}' does not contain content entry '{ContentDirectory}{contentFileName}'.");

            // Fixed lengths per the Graph fileEncryptionInfo resource
            // (https://learn.microsoft.com/graph/api/resources/intune-apps-fileencryptioninfo): IV must be
            // 16 bytes, Mac/MacKey must be 32 bytes. EncryptionKey and FileDigest have no declared fixed length.
            var encryptionInfo = new ContentEncryptionInfo(
                EncryptionKey: RequireBase64(encryptionInfoElement, "EncryptionKey", intuneWinPath),
                MacKey: RequireBase64WithLength(encryptionInfoElement, "MacKey", 32, intuneWinPath),
                InitializationVector: RequireBase64WithLength(encryptionInfoElement, "InitializationVector", 16, intuneWinPath),
                Mac: RequireBase64WithLength(encryptionInfoElement, "Mac", 32, intuneWinPath),
                ProfileIdentifier: RequireElementValue(encryptionInfoElement, "ProfileIdentifier", intuneWinPath),
                FileDigest: RequireBase64(encryptionInfoElement, "FileDigest", intuneWinPath),
                FileDigestAlgorithm: RequireElementValue(encryptionInfoElement, "FileDigestAlgorithm", intuneWinPath));

            var unencryptedContentSizeText = RequireElementValue(applicationInfo, "UnencryptedContentSize", intuneWinPath);
            if (!long.TryParse(unencryptedContentSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unencryptedContentSize)
                || unencryptedContentSize < 0)
            {
                throw new PackagingException(
                    $"'{intuneWinPath}': Detection.xml UnencryptedContentSize '{unencryptedContentSizeText}' is not a valid non-negative integer.");
            }

            return new IntuneWinContent(archive, contentEntry, contentFileName, encryptionInfo, unencryptedContentSize);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static string RequireElementValue(XElement parent, string elementName, string intuneWinPath)
        => parent.Element(elementName)?.Value
            ?? throw new PackagingException($"'{intuneWinPath}': Detection.xml is missing '{elementName}'.");

    private static byte[] RequireBase64(XElement parent, string elementName, string intuneWinPath)
    {
        var value = RequireElementValue(parent, elementName, intuneWinPath);
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new PackagingException($"'{intuneWinPath}': Detection.xml '{elementName}' is not valid base64.", ex);
        }
    }

    private static byte[] RequireBase64WithLength(XElement parent, string elementName, int expectedLength, string intuneWinPath)
    {
        var value = RequireBase64(parent, elementName, intuneWinPath);
        if (value.Length != expectedLength)
        {
            throw new PackagingException(
                $"'{intuneWinPath}': Detection.xml '{elementName}' must be {expectedLength} bytes but was {value.Length}.");
        }

        return value;
    }

    // ZIP entry names are '/'-separated per the ZIP format spec, but tolerate '\' defensively
    // since entry names are attacker/tool-controlled input, not something this codebase produces.
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string expectedPath)
    {
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName.Replace('\\', '/'), expectedPath, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
