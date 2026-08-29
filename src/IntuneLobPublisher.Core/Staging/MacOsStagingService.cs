using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Staging;

/// <summary>Result of staging one macOS app entry.</summary>
/// <param name="ContentFile">The staged <c>.pkg</c> file's path relative to <see cref="StagingDirectory"/>, i.e. <c>Source.Destination</c>.</param>
public sealed record MacOsStagingResult(
    string PackageIdentifier,
    string Platform,
    string Architecture,
    string StagingDirectory,
    string ContentFile,
    bool DryRun,
    string? SummaryPath,
    string? ExpectedSha256,
    string? ActualSha256);

/// <summary>Stages a macOS app entry (downloads its single <see cref="AppManifest.Source"/> item) into a directory.</summary>
public interface IMacOsStagingService
{
    Task<MacOsStagingResult> StageAsync(
        IntunePackageManifest manifest,
        AppManifest app,
        StagingOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Stages one macOS app entry: validates every manifest-supplied path, downloads the single
/// <see cref="AppManifest.Source"/> item through the existing source-provider registry, verifies its
/// SHA256 and writes staging-summary.json. Mirrors <see cref="WindowsStagingService"/>'s path-safety
/// and summary conventions, but there is only ever one file to stage - macOS has no
/// RepositoryFiles/ExternalFiles list, only the single unified <c>Source</c> item
/// (doc/01-manifest-schema.md §5.3). The summary never contains credentials.
/// </summary>
public sealed class MacOsStagingService : IMacOsStagingService
{
    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly SourceProviderRegistry _sourceProviders;
    private readonly ILogger<MacOsStagingService> _logger;

    public MacOsStagingService(SourceProviderRegistry sourceProviders, ILogger<MacOsStagingService> logger)
    {
        _sourceProviders = sourceProviders;
        _logger = logger;
    }

    public async Task<MacOsStagingResult> StageAsync(
        IntunePackageManifest manifest,
        AppManifest app,
        StagingOptions options,
        CancellationToken cancellationToken)
    {
        var source = app.Source
            ?? throw new StagingException("App entry has no Source definition.");
        var destination = source.Destination
            ?? throw new StagingException("Source.Destination is required for staging.");
        var packageIdentifier = manifest.PackageIdentifier
            ?? throw new StagingException("PackageIdentifier is required for staging.");
        var platform = app.Platform ?? throw new StagingException("Platform is required for staging.");
        // macOS resolves an omitted Architecture to "universal" (AppArchitecture.Resolve, issue #123);
        // Windows still requires it explicitly (this service is macOS-only, but Resolve is a no-op there).
        var architecture = AppArchitecture.Resolve(app) ?? throw new StagingException("Architecture is required for staging.");

        // Validate every path before touching the file system, same as WindowsStagingService.
        PathSafety.EnsureSafeDirectoryName(packageIdentifier, "PackageIdentifier");
        PathSafety.EnsureSafeRelativePath(destination, "Source.Destination");

        var appDirectory = Path.Combine(
            Path.GetFullPath(options.OutputDirectory),
            packageIdentifier,
            $"{platform}-{architecture}");
        var stagingDirectory = Path.Combine(appDirectory, "staging");

        _logger.LogInformation(
            "Staging {PackageIdentifier} {Platform}-{Architecture} (source {SourceType}) into {StagingDirectory}",
            packageIdentifier, platform, architecture, source.Type, stagingDirectory);

        if (options.DryRun)
        {
            _logger.LogInformation("[dry-run] would download {Type} source to {Destination}", source.Type, destination);
            return new MacOsStagingResult(
                packageIdentifier, platform, architecture, stagingDirectory, destination,
                DryRun: true, SummaryPath: null,
                ExpectedSha256: source.Sha256?.ToLowerInvariant(), ActualSha256: null);
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);

        var destinationFullPath = PathSafety.ResolveWithin(stagingDirectory, destination, "Source.Destination");
        var provider = _sourceProviders.Get(source.Type ?? string.Empty);
        var downloaded = await provider.DownloadAsync(new SourceDownloadRequest(source, destinationFullPath), cancellationToken)
            .ConfigureAwait(false);

        var expectedSha256 = source.Sha256
            ?? throw new StagingException("Source has no Sha256.");
        var actualSha256 = downloaded.Sha256;
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                $"SHA256 mismatch for '{destinationFullPath}'. Expected {expectedSha256.ToLowerInvariant()}, got {actualSha256}.");
        }

        _logger.LogInformation("SHA256 verified for {Destination}", destination);

        var summaryPath = Path.Combine(appDirectory, "staging-summary.json");
        var summary = new
        {
            PackageIdentifier = packageIdentifier,
            manifest.PackageName,
            manifest.PackageVersion,
            Platform = platform,
            Architecture = architecture,
            app.DisplayName,
            StagingDirectory = stagingDirectory,
            Source = new
            {
                source.Type,
                Destination = destination,
                ExpectedSha256 = expectedSha256.ToLowerInvariant(),
                ActualSha256 = actualSha256,
            },
        };
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, SummaryJsonOptions), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Staging summary written to {SummaryPath}", summaryPath);

        return new MacOsStagingResult(
            packageIdentifier, platform, architecture, stagingDirectory, destination,
            DryRun: false, summaryPath, expectedSha256.ToLowerInvariant(), actualSha256);
    }
}
