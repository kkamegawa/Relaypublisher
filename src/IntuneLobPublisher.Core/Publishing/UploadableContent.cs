namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The <c>fileEncryptionInfo</c> values a content upload commits to Graph
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-fileencryptioninfo). Shared by Windows
/// (parsed from a <c>.intunewin</c>'s Detection.xml) and macOS (computed in-process, since macOS has no
/// packaging tool that pre-encrypts content the way IntuneWinAppUtil does).
/// </summary>
public sealed record ContentEncryptionInfo(
    byte[] EncryptionKey,
    byte[] MacKey,
    byte[] InitializationVector,
    byte[] Mac,
    string ProfileIdentifier,
    byte[] FileDigest,
    string FileDigestAlgorithm);

/// <summary>
/// Content ready to upload as a Graph <c>mobileAppContentFile</c>: the encryption info needed to commit
/// the file, and a stream over the already-encrypted payload. Implementations own a backing resource (an
/// open ZIP archive, a temporary encrypted file) and must be disposed once the content has been read.
/// </summary>
public interface IUploadableContent : IDisposable
{
    /// <summary>The content file name recorded as the Graph <c>mobileAppContentFile.name</c>.</summary>
    string ContentFileName { get; }

    ContentEncryptionInfo EncryptionInfo { get; }

    /// <summary>Original (unencrypted) size in bytes.</summary>
    long UnencryptedContentSize { get; }

    /// <summary>Size of the encrypted payload in bytes.</summary>
    long EncryptedContentSize { get; }

    /// <summary>Opens the encrypted payload for reading. Callers should not call this more than once per instance.</summary>
    Stream OpenEncryptedContentStream();
}

/// <summary>
/// Produces <see cref="IUploadableContent"/> from a staged package file: unzip and parse the
/// already-encrypted content for a Windows <c>.intunewin</c> (<see cref="IntuneWinContentExtractor"/>),
/// or encrypt a macOS <c>.pkg</c> in-process (<see cref="PkgContentPreparer"/>).
/// </summary>
public interface IUploadableContentExtractor
{
    IUploadableContent Extract(string contentPath);
}
