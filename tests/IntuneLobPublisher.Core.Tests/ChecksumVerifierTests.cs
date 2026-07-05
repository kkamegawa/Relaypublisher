using System.Security.Cryptography;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Sources;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class ChecksumVerifierTests
{
    private static async Task<(string Path, string Sha256)> CreateFileAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"checksum-test-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, content);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return (path, sha256);
    }

    [TestMethod]
    public async Task VerifyFileAsync_CorrectSha256_Passes()
    {
        var (path, sha256) = await CreateFileAsync("payload");
        try
        {
            var actual = await ChecksumVerifier.VerifyFileAsync(path, sha256, CancellationToken.None);
            Assert.AreEqual(sha256, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task VerifyFileAsync_UppercaseExpected_PassesCaseInsensitively()
    {
        var (path, sha256) = await CreateFileAsync("payload");
        try
        {
            await ChecksumVerifier.VerifyFileAsync(path, sha256.ToUpperInvariant(), CancellationToken.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task VerifyFileAsync_WrongSha256_Throws()
    {
        var (path, _) = await CreateFileAsync("payload");
        try
        {
            await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(
                () => ChecksumVerifier.VerifyFileAsync(path, new string('0', 64), CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
