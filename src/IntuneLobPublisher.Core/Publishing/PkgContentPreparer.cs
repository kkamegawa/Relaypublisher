using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// A macOS <c>.pkg</c>, encrypted on the fly by <see cref="PkgContentPreparer"/>: the encryption info
/// needed to commit the file to Graph, and a stream over the encrypted payload. The encrypted payload
/// is written to a temporary file next to the source, deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class PkgContent : IUploadableContent
{
    private readonly string _encryptedPath;

    public PkgContent(string encryptedPath, string contentFileName, ContentEncryptionInfo encryptionInfo, long unencryptedContentSize)
    {
        _encryptedPath = encryptedPath;
        ContentFileName = contentFileName;
        EncryptionInfo = encryptionInfo;
        UnencryptedContentSize = unencryptedContentSize;
    }

    public string ContentFileName { get; }

    public ContentEncryptionInfo EncryptionInfo { get; }

    public long UnencryptedContentSize { get; }

    public long EncryptedContentSize => new FileInfo(_encryptedPath).Length;

    public Stream OpenEncryptedContentStream() => File.OpenRead(_encryptedPath);

    public void Dispose()
    {
        try
        {
            File.Delete(_encryptedPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Encrypts a staged, plaintext macOS <c>.pkg</c> for the Graph <c>mobileAppContentFile</c> upload flow.
/// Windows gets an already-encrypted <c>.intunewin</c> from IntuneWinAppUtil; macOS has no equivalent
/// tool, so this does the encryption in-process (doc/00-overview.md 6.13 / Phase 8 "macOS publisher").
/// AES-256-CBC with PKCS7 padding, matching what <see cref="IntuneWinContentExtractor"/> reads for
/// Windows content. Per the Graph <c>fileEncryptionInfo</c> resource
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-fileencryptioninfo), <c>mac</c> is
/// "the hash of the concatenation of the IV and encrypted file content" - HMAC-SHA256 keyed by
/// <c>macKey</c> - computed here over IV ‖ ciphertext, while only the ciphertext itself (no IV, no MAC)
/// is uploaded as the content stream, matching how the Win32 flow already separates the two: the
/// uploaded bytes are pure ciphertext, and IV/MAC/keys travel out of band in the commit call
/// (<see cref="IMobileAppContentClient.CommitFileAsync"/>).
/// </summary>
public sealed class PkgContentPreparer : IUploadableContentExtractor
{
    private const string ProfileIdentifier = "ProfileVersion1";
    private const string FileDigestAlgorithm = "SHA256";

    // Must be a multiple of the AES block size (16 bytes) so only the final chunk needs PKCS7 padding.
    private const int ChunkSizeBytes = 1024 * 1024;

    public IUploadableContent Extract(string contentPath)
    {
        if (!File.Exists(contentPath))
        {
            throw new PackagingException($"'{contentPath}' does not exist.");
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var macKey = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedPath = contentPath + $".{Guid.NewGuid():N}.encrypted";

        byte[] fileDigest;
        byte[] mac;
        try
        {
            (fileDigest, mac) = EncryptToFile(contentPath, encryptedPath, key, iv, macKey);
        }
        catch
        {
            TryDelete(encryptedPath);
            throw;
        }

        var encryptionInfo = new ContentEncryptionInfo(
            EncryptionKey: key,
            MacKey: macKey,
            InitializationVector: iv,
            Mac: mac,
            ProfileIdentifier: ProfileIdentifier,
            FileDigest: fileDigest,
            FileDigestAlgorithm: FileDigestAlgorithm);

        return new PkgContent(encryptedPath, Path.GetFileName(contentPath), encryptionInfo, new FileInfo(contentPath).Length);
    }

    /// <summary>
    /// Streams <paramref name="contentPath"/> through AES-CBC encryption into <paramref name="encryptedPath"/>
    /// in bounded-size chunks (rather than buffering the whole file) since a PKG may be up to 8 GB
    /// (doc/00-overview.md §6.13). Computes the plaintext SHA256 digest and the IV ‖ ciphertext HMAC in
    /// the same pass.
    /// </summary>
    private static (byte[] FileDigest, byte[] Mac) EncryptToFile(
        string contentPath, string encryptedPath, byte[] key, byte[] iv, byte[] macKey)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        using var sha256 = SHA256.Create();
        using var hmac = new HMACSHA256(macKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);

        using var input = File.OpenRead(contentPath);
        using var output = File.Create(encryptedPath);

        var readBuffer = new byte[ChunkSizeBytes];
        var writeBuffer = new byte[ChunkSizeBytes];
        int bytesRead;

        // Only the final chunk can be shorter than a full buffer, since ReadFullyOrToEnd only returns
        // a short read once the stream is genuinely exhausted.
        while ((bytesRead = ReadFullyOrToEnd(input, readBuffer)) == ChunkSizeBytes)
        {
            sha256.TransformBlock(readBuffer, 0, bytesRead, null, 0);
            var written = encryptor.TransformBlock(readBuffer, 0, bytesRead, writeBuffer, 0);
            output.Write(writeBuffer, 0, written);
            hmac.TransformBlock(writeBuffer, 0, written, null, 0);
        }

        sha256.TransformFinalBlock(readBuffer, 0, bytesRead);
        var fileDigest = sha256.Hash!;

        var finalBlock = encryptor.TransformFinalBlock(readBuffer, 0, bytesRead);
        output.Write(finalBlock);
        hmac.TransformBlock(finalBlock, 0, finalBlock.Length, null, 0);
        hmac.TransformFinalBlock([], 0, 0);
        var mac = hmac.Hash!;

        return (fileDigest, mac);
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream ends, since <see cref="Stream.Read"/> may return short reads before EOF.</summary>
    private static int ReadFullyOrToEnd(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
