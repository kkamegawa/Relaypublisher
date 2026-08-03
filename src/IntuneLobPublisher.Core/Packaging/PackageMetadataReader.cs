using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// The package artifacts publish consumes: the parsed metadata and the resolved full path of the
/// content file - the <c>.intunewin</c> for Windows, or the staged <c>.pkg</c> for macOS.
/// </summary>
public sealed record PackageArtifacts(PackageMetadata Metadata, string ContentPath);

/// <summary>
/// Reads <c>&lt;packageDir&gt;/&lt;PackageIdentifier&gt;/&lt;platform&gt;-&lt;architecture&gt;/package-metadata.json</c>
/// written by <see cref="IntuneWinPackager"/> and resolves the <c>.intunewin</c> it references.
/// The file name stored in the metadata goes through <see cref="PathSafety.ResolveWithin"/> so a
/// tampered metadata file cannot point outside its package directory.
/// </summary>
public static class PackageMetadataReader
{
    public static async Task<PackageArtifacts> ReadAsync(
        string packageDirectory,
        AppIdentity identity,
        CancellationToken cancellationToken)
    {
        PathSafety.EnsureSafeDirectoryName(identity.PackageIdentifier, "PackageIdentifier");
        PathSafety.EnsureSafeDirectoryName(identity.Platform, "Platform");
        PathSafety.EnsureSafeDirectoryName(identity.Architecture, "Architecture");

        var entryDirectory = Path.Combine(
            Path.GetFullPath(packageDirectory),
            identity.PackageIdentifier,
            $"{identity.Platform}-{identity.Architecture}");
        var metadataPath = Path.Combine(entryDirectory, PackageMetadataJson.FileName);
        if (!File.Exists(metadataPath))
        {
            throw new PackagingException(
                $"Package metadata '{metadataPath}' does not exist. Run the package command for " +
                $"'{identity.PackageIdentifier}' {identity.Platform}-{identity.Architecture} first.");
        }

        PackageMetadata? metadata;
        try
        {
            await using var stream = File.OpenRead(metadataPath);
            metadata = await JsonSerializer
                .DeserializeAsync<PackageMetadata>(stream, PackageMetadataJson.SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PackagingException($"Package metadata '{metadataPath}' is not valid JSON: {exception.Message}");
        }

        // Windows writes IntuneWinFile, macOS writes ContentFile; exactly one is expected to be present.
        var contentFile = metadata?.ContentFile ?? metadata?.IntuneWinFile;
        if (metadata is null || string.IsNullOrWhiteSpace(contentFile) || string.IsNullOrWhiteSpace(metadata.InputHash))
        {
            throw new PackagingException(
                $"Package metadata '{metadataPath}' is missing required fields (contentFile/intuneWinFile, inputHash).");
        }

        // Same comparison rules as AppIdentity.Matches: the identifier is a stable case-sensitive
        // manifest key; platform/architecture casing may differ between writers.
        var matchesIdentity =
            string.Equals(identity.PackageIdentifier, metadata.PackageIdentifier, StringComparison.Ordinal)
            && string.Equals(identity.Platform, metadata.Platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(identity.Architecture, metadata.Architecture, StringComparison.OrdinalIgnoreCase);
        if (!matchesIdentity)
        {
            throw new PackagingException(
                $"Package metadata '{metadataPath}' was written for " +
                $"'{metadata.PackageIdentifier}' {metadata.Platform}-{metadata.Architecture}, not for " +
                $"'{identity.PackageIdentifier}' {identity.Platform}-{identity.Architecture}.");
        }

        var contentPath = PathSafety.ResolveWithin(entryDirectory, contentFile, "Package metadata ContentFile/IntuneWinFile");
        if (!File.Exists(contentPath))
        {
            throw new PackagingException(
                $"Package '{contentPath}' referenced by '{metadataPath}' does not exist. Re-run the package command.");
        }

        return new PackageArtifacts(metadata, contentPath);
    }
}
