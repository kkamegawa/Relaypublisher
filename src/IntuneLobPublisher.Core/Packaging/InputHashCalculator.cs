using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Deterministic input hash (doc/00-overview.md 6.7): SHA256 over the normalized manifest
/// hash plus each staged file's relative path and SHA256, sorted by path. The generated
/// .intunewin is encrypted with a random key per run, so its hash is never used for
/// identity or skip decisions - only this input hash is.
/// </summary>
public static class InputHashCalculator
{
    // Canonical serialization: fixed property order (model declaration order), camelCase,
    // nulls dropped, no whitespace. YAML formatting differences therefore do not change the hash.
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>SHA256 (lowercase hex) of the canonical JSON form of the manifest.</summary>
    public static string ComputeManifestHash(IntunePackageManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, CanonicalJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// SHA256 (lowercase hex) over the manifest hash and every file under
    /// <paramref name="stagingDirectory"/> as "relative-path\nsha256" entries sorted ordinally
    /// by path. Paths use '/' separators so the hash is identical across operating systems.
    /// </summary>
    public static async Task<string> ComputeInputHashAsync(
        IntunePackageManifest manifest,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var entries = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sha256 = await ChecksumVerifier.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false);
            entries.Add($"{relativePath}\n{sha256}");
        }

        entries.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder(ComputeManifestHash(manifest));
        foreach (var entry in entries)
        {
            builder.Append('\n').Append(entry);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
