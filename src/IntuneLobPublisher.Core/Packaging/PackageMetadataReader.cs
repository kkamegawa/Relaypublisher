using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Sources;
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
    private const int CurrentMacOsMetadataSchemaVersion = 2;

    /// <summary>
    /// Reads and verifies a macOS package artifact without downloading its source again. In addition
    /// to the normal metadata/path checks, this overload hashes the current bytes, verifies the stored
    /// byte length and SHA256, and re-runs the bounded XAR inspector. The saved report is compared with
    /// the fresh archive facts; manifest-aware warning acknowledgement remains the responsibility of
    /// the publish preflight layer.
    /// </summary>
    public static async Task<PackageArtifacts> ReadAndVerifyAsync(
        string packageDirectory,
        AppIdentity identity,
        IPkgBundleInspector inspector,
        CancellationToken cancellationToken)
        => await ReadAndVerifyCoreAsync(
            packageDirectory,
            identity,
            null,
            null,
            inspector,
            null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads and verifies a macOS artifact and, when the manifest/app are supplied, reconstructs the
    /// manifest-aware inspection report so selected-primary and warning fields cannot be edited out of
    /// the artifact metadata. <paramref name="expectedCliVersion"/> is optional so library callers can
    /// verify a package produced by a separately pinned CLI; the publish command should provide it.
    /// </summary>
    public static Task<PackageArtifacts> ReadAndVerifyAsync(
        string packageDirectory,
        AppIdentity identity,
        IntunePackageManifest manifest,
        AppManifest app,
        IPkgBundleInspector inspector,
        string? expectedCliVersion,
        CancellationToken cancellationToken)
        => ReadAndVerifyCoreAsync(
            packageDirectory,
            identity,
            manifest,
            app,
            inspector,
            expectedCliVersion,
            cancellationToken);

    private static async Task<PackageArtifacts> ReadAndVerifyCoreAsync(
        string packageDirectory,
        AppIdentity identity,
        IntunePackageManifest? manifest,
        AppManifest? app,
        IPkgBundleInspector inspector,
        string? expectedCliVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        var artifacts = await ReadAsync(packageDirectory, identity, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(identity.Platform, "macos", StringComparison.OrdinalIgnoreCase))
        {
            return artifacts;
        }

        var metadata = artifacts.Metadata;
        if (metadata.MetadataSchemaVersion != CurrentMacOsMetadataSchemaVersion ||
            string.IsNullOrWhiteSpace(metadata.ContentSha256) ||
            metadata.ContentSize is null ||
            string.IsNullOrWhiteSpace(metadata.CliVersion) ||
            metadata.Inspection is null ||
            metadata.Inspection.Inspection is null ||
            metadata.Inspection.Inspection.Bundles is null ||
            metadata.Inspection.Warnings is null)
        {
            throw new PackagingException(
                "macOS package metadata has an unsupported schema or is missing " +
                "contentSha256, contentSize, cliVersion, or inspection. " +
                "Run the package command again with the current CLI.");
        }

        if (expectedCliVersion is not null &&
            !string.Equals(metadata.CliVersion, expectedCliVersion, StringComparison.Ordinal))
        {
            throw new PackagingException(
                $"The staged macOS package was produced by CLI '{metadata.CliVersion}', " +
                $"but publish requires CLI '{expectedCliVersion}'.");
        }

        await using var stream = new FileStream(
            artifacts.ContentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != metadata.ContentSize.Value)
        {
            throw new PackagingException(
                $"The staged macOS package '{artifacts.ContentPath}' has size {stream.Length}, " +
                $"but package metadata records {metadata.ContentSize.Value}.");
        }

        var actualSha256 = await ChecksumVerifier
            .ComputeSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(actualSha256, metadata.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackagingException(
                $"The staged macOS package '{artifacts.ContentPath}' has SHA256 {actualSha256}, " +
                $"but package metadata records {metadata.ContentSha256}.");
        }

        if (app is not null && string.IsNullOrWhiteSpace(app.Source?.Sha256))
        {
            throw new PackagingException("The macOS manifest is missing Source.Sha256.");
        }

        if (app is not null &&
            !string.Equals(actualSha256, app.Source!.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackagingException(
                $"The staged macOS package '{artifacts.ContentPath}' has SHA256 {actualSha256}, " +
                "but the manifest declares a different Source.Sha256. Run package again.");
        }

        stream.Position = 0;
        var freshInspection = await inspector.InspectAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var postInspectionSha256 = await ChecksumVerifier
            .ComputeSha256Async(stream, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(postInspectionSha256, actualSha256, StringComparison.Ordinal))
        {
            throw new PackagingException("The staged macOS package changed during XAR inspection.");
        }

        var savedInspection = metadata.Inspection.Inspection;
        if (!InspectionFactsEqual(savedInspection, freshInspection))
        {
            throw new PackagingException(
                "The staged macOS package inspection does not match package metadata. " +
                "The artifact or inspection report is stale; run package again.");
        }

        if (manifest is not null || app is not null)
        {
            if (manifest is null || app is null)
            {
                throw new ArgumentException("Manifest and app must be supplied together.");
            }

            var freshReport = MacOsPkgInspectionPolicy.CreateReport(
                manifest,
                app,
                freshInspection,
                metadata.Inspection.ForceAcknowledged);
            if (!InspectionReportsEqual(metadata.Inspection, freshReport))
            {
                throw new PackagingException(
                    "The staged macOS package inspection report does not match the manifest or current " +
                    "archive facts. Run package again.");
            }
        }

        return artifacts;
    }

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

    private static bool InspectionFactsEqual(
        PkgBundleInspectionResult expected,
        PkgBundleInspectionResult actual)
    {
        if (!string.Equals(expected.InspectorVersion, actual.InspectorVersion, StringComparison.Ordinal) ||
            expected.Bundles.Count != actual.Bundles.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Bundles.Count; index++)
        {
            var expectedBundle = expected.Bundles[index];
            var actualBundle = actual.Bundles[index];
            if (!string.Equals(expectedBundle.BundleId, actualBundle.BundleId, StringComparison.Ordinal) ||
                !string.Equals(expectedBundle.BundleVersion, actualBundle.BundleVersion, StringComparison.Ordinal) ||
                !string.Equals(expectedBundle.BundleBuildVersion, actualBundle.BundleBuildVersion, StringComparison.Ordinal) ||
                !string.Equals(expectedBundle.SourceEntry, actualBundle.SourceEntry, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool InspectionReportsEqual(PkgInspectionReport expected, PkgInspectionReport actual)
    {
        if (!string.Equals(expected.SelectedPrimaryBundleId, actual.SelectedPrimaryBundleId, StringComparison.Ordinal) ||
            expected.ForceAcknowledged != actual.ForceAcknowledged ||
            expected.Warnings.Count != actual.Warnings.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Warnings.Count; index++)
        {
            var expectedWarning = expected.Warnings[index];
            var actualWarning = actual.Warnings[index];
            if (expectedWarning.Code != actualWarning.Code ||
                !string.Equals(expectedWarning.BundleId, actualWarning.BundleId, StringComparison.Ordinal) ||
                !string.Equals(expectedWarning.Detail, actualWarning.Detail, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return InspectionFactsEqual(expected.Inspection, actual.Inspection);
    }
}
