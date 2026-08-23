using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class PkgContentPreparerTests
{
    // HMAC-SHA256 (32 bytes) + AES IV (16 bytes): the header PkgContentPreparer must prepend to the
    // ciphertext so the uploaded stream matches Intune's expected [mac][iv][ciphertext] layout.
    private const int HeaderLengthBytes = 32 + 16;

    private DirectoryInfo _workspace = null!;

    [TestInitialize]
    public void Initialize() => _workspace = Directory.CreateTempSubdirectory("pkg-content-preparer-tests-");

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private string WritePlaintext(byte[] content)
    {
        var path = Path.Combine(_workspace.FullName, "contoso-tool-arm64.pkg");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] ReadUploadedBytes(IUploadableContent content)
    {
        using var stream = content.OpenEncryptedContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Splits the uploaded stream into its [mac][iv] header and the ciphertext that follows.</summary>
    private static (byte[] Mac, byte[] Iv, byte[] Ciphertext) SplitHeaderAndCiphertext(byte[] uploadedBytes)
    {
        var mac = uploadedBytes[..32];
        var iv = uploadedBytes[32..48];
        var ciphertext = uploadedBytes[48..];
        return (mac, iv, ciphertext);
    }

    private static byte[] Decrypt(byte[] ciphertext, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    [TestMethod]
    public void Extract_SmallFile_UploadedStreamStartsWithMacThenIv()
    {
        // Per Intune's expected upload layout (matching the .intunewin content entry, and Microsoft's
        // own reference implementation in microsoftgraph/powershell-intune-samples'
        // Application_LOB_Add.ps1 EncryptFileWithIV): the uploaded bytes are [mac][iv][ciphertext].
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var uploadedBytes = ReadUploadedBytes(content);
        var (mac, iv, _) = SplitHeaderAndCiphertext(uploadedBytes);

        CollectionAssert.AreEqual(content.EncryptionInfo.Mac, mac);
        CollectionAssert.AreEqual(content.EncryptionInfo.InitializationVector, iv);
    }

    [TestMethod]
    public void Extract_SmallFile_DecryptsBackToOriginalPlaintext()
    {
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var (_, _, ciphertext) = SplitHeaderAndCiphertext(ReadUploadedBytes(content));

        var decrypted = Decrypt(ciphertext, content.EncryptionInfo.EncryptionKey, content.EncryptionInfo.InitializationVector);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public void Extract_SmallFile_MacIsHmacOfIvConcatenatedWithCiphertext()
    {
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var (_, _, ciphertext) = SplitHeaderAndCiphertext(ReadUploadedBytes(content));

        // Per the Graph fileEncryptionInfo resource: "mac" is the HMAC (keyed by macKey) of IV ‖ ciphertext.
        using var hmac = new HMACSHA256(content.EncryptionInfo.MacKey);
        var expectedMac = hmac.ComputeHash([.. content.EncryptionInfo.InitializationVector, .. ciphertext]);

        CollectionAssert.AreEqual(expectedMac, content.EncryptionInfo.Mac);
    }

    [TestMethod]
    public void Extract_SmallFile_FileDigestIsSha256OfPlaintext()
    {
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);

        CollectionAssert.AreEqual(SHA256.HashData(plaintext), content.EncryptionInfo.FileDigest);
        Assert.AreEqual("SHA256", content.EncryptionInfo.FileDigestAlgorithm);
        Assert.AreEqual("ProfileVersion1", content.EncryptionInfo.ProfileIdentifier);
    }

    [TestMethod]
    public void Extract_SmallFile_ReportsCorrectSizesAndFileName()
    {
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);

        Assert.AreEqual("contoso-tool-arm64.pkg", content.ContentFileName);
        Assert.AreEqual(plaintext.Length, content.UnencryptedContentSize);

        var uploadedBytes = ReadUploadedBytes(content);
        Assert.AreEqual(uploadedBytes.Length, content.EncryptedContentSize);
    }

    [TestMethod]
    public void Extract_SmallFile_EncryptedContentSizeIncludesHeaderPlusCiphertext()
    {
        // sizeEncrypted reported to Graph must be the whole uploaded file's length - header included -
        // not just the ciphertext length, or Intune rejects the mismatch between declared and actual size.
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var (_, _, ciphertext) = SplitHeaderAndCiphertext(ReadUploadedBytes(content));

        Assert.AreEqual(HeaderLengthBytes + ciphertext.Length, content.EncryptedContentSize);
    }

    [TestMethod]
    public void Extract_KeysAreRandomPerCall()
    {
        var path = WritePlaintext("same-content"u8.ToArray());
        var preparer = new PkgContentPreparer();

        using var first = preparer.Extract(path);
        using var second = preparer.Extract(path);

        CollectionAssert.AreNotEqual(first.EncryptionInfo.EncryptionKey, second.EncryptionInfo.EncryptionKey);
        CollectionAssert.AreNotEqual(first.EncryptionInfo.InitializationVector, second.EncryptionInfo.InitializationVector);
    }

    [TestMethod]
    public void Extract_MultiChunkFile_DecryptsBackToOriginalPlaintext()
    {
        // Larger than the preparer's internal chunk size (1 MiB) so the streaming loop exercises
        // more than one full-chunk iteration before the final (possibly zero-length) block.
        var plaintext = new byte[2 * 1024 * 1024 + 137];
        Random.Shared.NextBytes(plaintext);
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var (_, _, ciphertext) = SplitHeaderAndCiphertext(ReadUploadedBytes(content));

        var decrypted = Decrypt(ciphertext, content.EncryptionInfo.EncryptionKey, content.EncryptionInfo.InitializationVector);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public void Extract_FileExactlyOnChunkBoundary_DecryptsBackToOriginalPlaintext()
    {
        // Exactly a multiple of the 1 MiB chunk size: ReadFullyOrToEnd's streaming loop reads a final
        // full-size chunk and must still emit a (zero-length-input) PKCS7 padding block correctly,
        // rather than losing or duplicating the boundary chunk.
        var plaintext = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(plaintext);
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        var (_, _, ciphertext) = SplitHeaderAndCiphertext(ReadUploadedBytes(content));

        var decrypted = Decrypt(ciphertext, content.EncryptionInfo.EncryptionKey, content.EncryptionInfo.InitializationVector);
        CollectionAssert.AreEqual(plaintext, decrypted);
    }

    [TestMethod]
    public void Extract_MissingFile_ThrowsPackagingException()
    {
        var preparer = new PkgContentPreparer();

        Assert.ThrowsExactly<PackagingException>(
            () => preparer.Extract(Path.Combine(_workspace.FullName, "missing.pkg")));
    }

    [TestMethod]
    public void Dispose_DeletesTheEncryptedTempFile()
    {
        var path = WritePlaintext("fake-pkg-binary-content"u8.ToArray());
        var preparer = new PkgContentPreparer();
        var content = preparer.Extract(path);

        Assert.IsTrue(content.EncryptedContentSize > 0, "Sanity check: the encrypted temp file exists before Dispose.");

        content.Dispose();

        // EncryptedContentSize re-reads the temp file's length on every access, so after Dispose
        // (which deletes it) this throws instead of returning a stale value.
        Assert.ThrowsExactly<FileNotFoundException>(() => _ = content.EncryptedContentSize);
    }
}
