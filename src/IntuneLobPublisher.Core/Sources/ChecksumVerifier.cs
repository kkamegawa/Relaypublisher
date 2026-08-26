using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>SHA256 helpers. Comparison is case-insensitive.</summary>
public static class ChecksumVerifier
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Computes the file hash and throws when it does not match <paramref name="expectedSha256"/>.</summary>
    /// <returns>The actual SHA256 (lowercase hex).</returns>
    public static async Task<string> VerifyFileAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                $"SHA256 mismatch for '{path}'. Expected {expectedSha256.ToLowerInvariant()}, got {actual}.");
        }

        return actual;
    }
}
