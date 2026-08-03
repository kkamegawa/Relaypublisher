using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>Result of writing package-metadata.json for one staged macOS app entry.</summary>
public sealed record MacOsPackageResult(
    string PackageIdentifier,
    string Platform,
    string Architecture,
    string ContentPath,
    string ContentSha256,
    string InputHash,
    string MetadataPath);

/// <summary>Writes package-metadata.json for a completed macOS staging run.</summary>
public interface IMacOsPackager
{
    Task<MacOsPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        MacOsStagingResult stagingResult,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes package-metadata.json for a macOS app entry. Unlike <see cref="IntuneWinPackager"/> there is no
/// external tool step here: the staged <c>.pkg</c> downloaded and SHA256-verified by
/// <see cref="MacOsStagingService"/> already is the final package artifact. Content encryption for the
/// Graph upload happens later, at publish time (doc/00-overview.md 6.13 / Phase 8 "macOS publisher").
/// </summary>
public sealed class MacOsPackager : IMacOsPackager
{
    private readonly ILogger<MacOsPackager> _logger;

    public MacOsPackager(ILogger<MacOsPackager> logger)
    {
        _logger = logger;
    }

    public async Task<MacOsPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        MacOsStagingResult stagingResult,
        CancellationToken cancellationToken)
    {
        if (stagingResult.DryRun)
        {
            throw new PackagingException("Cannot write package metadata from a dry-run staging result.");
        }

        var stagingDirectory = Path.GetFullPath(stagingResult.StagingDirectory);
        var contentPath = PathSafety.ResolveWithin(stagingDirectory, stagingResult.ContentFile, "Source.Destination");
        if (!File.Exists(contentPath))
        {
            throw new PackagingException($"Staged package '{contentPath}' does not exist.");
        }

        // The .intunewin/.pkg and its metadata go next to the staging directory
        // (<output>/<PackageIdentifier>/<platform>-<architecture>/), same layout as Windows.
        var outputDirectory = Path.GetDirectoryName(stagingDirectory)!;

        var inputHash = await InputHashCalculator.ComputeInputHashAsync(manifest, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var contentSha256 = stagingResult.ActualSha256
            ?? await ChecksumVerifier.ComputeSha256Async(contentPath, cancellationToken).ConfigureAwait(false);

        // Stored relative to outputDirectory (the metadata file's own directory), matching how
        // PackageMetadataReader resolves IntuneWinFile for Windows packages.
        var contentFileRelative = Path.GetRelativePath(outputDirectory, contentPath).Replace('\\', '/');

        var metadataPath = Path.Combine(outputDirectory, PackageMetadataJson.FileName);
        var metadata = new PackageMetadata(
            stagingResult.PackageIdentifier,
            manifest.PackageVersion,
            stagingResult.Platform,
            stagingResult.Architecture,
            inputHash,
            Tool: null,
            IntuneWinFile: null,
            IntuneWinSha256: null,
            DateTimeOffset.UtcNow,
            ContentFile: contentFileRelative,
            ContentSha256: contentSha256);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, PackageMetadataJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Package metadata written to {MetadataPath}", metadataPath);

        return new MacOsPackageResult(
            stagingResult.PackageIdentifier,
            stagingResult.Platform,
            stagingResult.Architecture,
            contentPath,
            contentSha256,
            inputHash,
            metadataPath);
    }
}
