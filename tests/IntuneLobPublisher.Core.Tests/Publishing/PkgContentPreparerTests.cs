using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class PkgContentPreparerTests
{
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
    public void Extract_SmallFile_DecryptsBackToOriginalPlaintext()
    {
        var plaintext = "fake-pkg-binary-content"u8.ToArray();
        var path = WritePlaintext(plaintext);
        var preparer = new PkgContentPreparer();

        using var content = preparer.Extract(path);
        using var stream = content.OpenEncryptedContentStream();
        using var ciphertextStream = new MemoryStream();
        stream.CopyTo(ciphertextStream);
        var ciphertext = ciphertextStream.ToArray();

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
        using var stream = content.OpenEncryptedContentStream();
        using var ciphertextStream = new MemoryStream();
        stream.CopyTo(ciphertextStream);
        var ciphertext = ciphertextStream.ToArray();

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

        using var stream = content.OpenEncryptedContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.AreEqual(buffer.Length, content.EncryptedContentSize);
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
        using var stream = content.OpenEncryptedContentStream();
        using var ciphertextStream = new MemoryStream();
        stream.CopyTo(ciphertextStream);

        var decrypted = Decrypt(ciphertextStream.ToArray(), content.EncryptionInfo.EncryptionKey, content.EncryptionInfo.InitializationVector);
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
