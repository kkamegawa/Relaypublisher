using System.Text.Json;
using System.Reflection;
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
    string MetadataPath,
    long ContentSize = 0,
    string? CliVersion = null,
    PkgInspectionReport? Inspection = null);

/// <summary>Writes package-metadata.json for a completed macOS staging run.</summary>
public interface IMacOsPackager
{
    Task<MacOsPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        MacOsStagingResult stagingResult,
        CancellationToken cancellationToken,
        bool forceAcknowledged = false,
        string? cliVersion = null);
}

/// <summary>
/// Writes package-metadata.json for a macOS app entry. Unlike <see cref="IntuneWinPackager"/> there is no
/// external tool step here: the staged <c>.pkg</c> downloaded and SHA256-verified by
/// <see cref="MacOsStagingService"/> already is the final package artifact. Content encryption for the
/// Graph upload happens later, at publish time (doc/00-overview.md 6.13 / Phase 8 "macOS publisher").
/// </summary>
public sealed class MacOsPackager : IMacOsPackager
{
    private const int PackageMetadataSchemaVersion = 2;

    private readonly ILogger<MacOsPackager> _logger;
    private readonly IPkgBundleInspector _inspector;

    public MacOsPackager(ILogger<MacOsPackager> logger, IPkgBundleInspector inspector)
    {
        _logger = logger;
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public async Task<MacOsPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        MacOsStagingResult stagingResult,
        CancellationToken cancellationToken,
        bool forceAcknowledged = false,
        string? cliVersion = null)
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

        var app = ResolveApp(manifest, stagingResult);
        var expectedSha256 = app.Source?.Sha256;
        if (!IsSha256(expectedSha256))
        {
            throw new PackagingException("The macOS manifest Source.Sha256 must be exactly 64 hexadecimal characters.");
        }

        if (!string.IsNullOrWhiteSpace(stagingResult.ExpectedSha256) &&
            !string.Equals(stagingResult.ExpectedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                "The staging SHA256 does not match the macOS manifest Source.Sha256.");
        }

        var inputHash = await InputHashCalculator.ComputeInputHashAsync(manifest, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        // Recompute the digest from the bytes that will be inspected and published. The staging
        // result is evidence that the source provider verified its download, but it must not become
        // the trust root if the staged file changed before package creation.
        await using var packageStream = new FileStream(
            contentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentSize = packageStream.Length;
        var contentSha256 = await ChecksumVerifier.ComputeSha256Async(packageStream, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(contentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                $"SHA256 mismatch for staged package '{contentPath}'. Expected " +
                $"{expectedSha256!.ToLowerInvariant()}, got {contentSha256}.");
        }

        packageStream.Position = 0;
        var inspection = await _inspector.InspectAsync(packageStream, cancellationToken).ConfigureAwait(false);
        packageStream.Position = 0;
        var postInspectionSha256 = await ChecksumVerifier.ComputeSha256Async(packageStream, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(postInspectionSha256, contentSha256, StringComparison.Ordinal))
        {
            throw new PackagingException("The staged macOS package changed during XAR inspection.");
        }

        var report = MacOsPkgInspectionPolicy.CreateReport(
            manifest, app, inspection, forceAcknowledged);
        var producerVersion = cliVersion ?? GetCurrentCliVersion();

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
            ContentSha256: contentSha256,
            ContentSize: contentSize,
            CliVersion: producerVersion,
            Inspection: report,
            MetadataSchemaVersion: PackageMetadataSchemaVersion);
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
            metadataPath,
            contentSize,
            producerVersion,
            report);
    }

    private static string GetCurrentCliVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "unknown";
    }

    // The staging result intentionally carries only filesystem/source facts. MacOsPackager is used
    // by the package command after manifest validation, so resolve the corresponding app entry here
    // without adding a second staging-to-manifest DTO to the public API.
    private static AppManifest ResolveApp(IntunePackageManifest manifest, MacOsStagingResult stagingResult)
        => manifest.Apps.FirstOrDefault(app =>
                string.Equals(app.Platform, stagingResult.Platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(app.Architecture, stagingResult.Architecture, StringComparison.OrdinalIgnoreCase))
            ?? throw new PackagingException(
                $"Manifest does not contain macOS entry '{stagingResult.Platform}-{stagingResult.Architecture}'.");

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
