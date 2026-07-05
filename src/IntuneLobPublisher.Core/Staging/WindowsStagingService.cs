using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Staging;

/// <summary>
/// Stages one Windows app entry: validates every manifest-supplied path, copies repository
/// files, downloads external files through source providers, verifies checksums, checks the
/// setup file and writes staging-summary.json. The summary never contains credentials.
/// </summary>
public sealed class WindowsStagingService : IWindowsStagingService
{
    private static readonly JsonSerializerOptions SummaryJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly SourceProviderRegistry _sourceProviders;
    private readonly ILogger<WindowsStagingService> _logger;

    public WindowsStagingService(SourceProviderRegistry sourceProviders, ILogger<WindowsStagingService> logger)
    {
        _sourceProviders = sourceProviders;
        _logger = logger;
    }

    public async Task<StagingResult> StageAsync(
        IntunePackageManifest manifest,
        AppManifest app,
        StagingOptions options,
        CancellationToken cancellationToken)
    {
        var package = app.Package
            ?? throw new StagingException("App entry has no Package definition.");
        var setupFile = package.IntuneWin?.SetupFile
            ?? throw new StagingException("Package.IntuneWin.SetupFile is required for staging.");
        var packageIdentifier = manifest.PackageIdentifier
            ?? throw new StagingException("PackageIdentifier is required for staging.");
        var platform = app.Platform ?? throw new StagingException("Platform is required for staging.");
        var architecture = app.Architecture ?? throw new StagingException("Architecture is required for staging.");

        PathSafety.EnsureSafeDirectoryName(packageIdentifier, "PackageIdentifier");
        var appDirectory = Path.Combine(
            Path.GetFullPath(options.OutputDirectory),
            packageIdentifier,
            $"{platform}-{architecture}");
        var stagingDirectory = Path.Combine(appDirectory, "staging");

        // Validate every path before touching the file system so a malicious manifest
        // fails before any partial staging output exists.
        PathSafety.EnsureSafeRelativePath(setupFile, "Package.IntuneWin.SetupFile");

        var repositoryCopies = new List<(string SourceFullPath, string SourceRelative, string DestinationRelative)>();
        foreach (var file in package.RepositoryFiles)
        {
            var source = file.Source ?? throw new StagingException("RepositoryFiles item has no Source.");
            var destination = file.Destination ?? throw new StagingException("RepositoryFiles item has no Destination.");
            var sourceFullPath = PathSafety.ResolveWithin(options.RepositoryRoot, source, "RepositoryFiles.Source");
            PathSafety.EnsureSafeRelativePath(destination, "RepositoryFiles.Destination");
            if (!File.Exists(sourceFullPath))
            {
                throw new StagingException($"Repository file '{source}' does not exist under '{options.RepositoryRoot}'.");
            }

            repositoryCopies.Add((sourceFullPath, source, destination));
        }

        foreach (var external in package.ExternalFiles)
        {
            var destination = external.Destination ?? throw new StagingException("ExternalFiles item has no Destination.");
            PathSafety.EnsureSafeRelativePath(destination, "ExternalFiles.Destination");
        }

        if (app.Detection?.ScriptFile is { } scriptFile)
        {
            var scriptFullPath = PathSafety.ResolveWithin(options.RepositoryRoot, scriptFile, "Detection.ScriptFile");
            if (!File.Exists(scriptFullPath))
            {
                throw new StagingException($"Detection script '{scriptFile}' does not exist under '{options.RepositoryRoot}'.");
            }
        }

        _logger.LogInformation(
            "Staging {PackageIdentifier} {Platform}-{Architecture} ({RepositoryFileCount} repository files, {ExternalFileCount} external files) into {StagingDirectory}",
            packageIdentifier, platform, architecture,
            repositoryCopies.Count, package.ExternalFiles.Count, stagingDirectory);

        if (options.DryRun)
        {
            return BuildDryRunResult(packageIdentifier, platform, architecture, stagingDirectory, setupFile, repositoryCopies, package);
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        Directory.CreateDirectory(stagingDirectory);

        var stagedRepositoryFiles = new List<StagedRepositoryFile>();
        foreach (var (sourceFullPath, sourceRelative, destinationRelative) in repositoryCopies)
        {
            var destinationFullPath = PathSafety.ResolveWithin(stagingDirectory, destinationRelative, "RepositoryFiles.Destination");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFullPath)!);
            File.Copy(sourceFullPath, destinationFullPath, overwrite: true);
            _logger.LogInformation("Copied {Source} -> {Destination}", sourceRelative, destinationRelative);
            stagedRepositoryFiles.Add(new StagedRepositoryFile(sourceRelative, destinationRelative));
        }

        var stagedExternalFiles = new List<StagedExternalFile>();
        foreach (var external in package.ExternalFiles)
        {
            var destinationRelative = external.Destination!;
            var destinationFullPath = PathSafety.ResolveWithin(stagingDirectory, destinationRelative, "ExternalFiles.Destination");
            var provider = _sourceProviders.Get(external.Type ?? string.Empty);
            var downloaded = await provider.DownloadAsync(new SourceDownloadRequest(external, destinationFullPath), cancellationToken)
                .ConfigureAwait(false);

            var expectedSha256 = external.Sha256
                ?? throw new StagingException($"ExternalFiles item '{destinationRelative}' has no Sha256.");

            var actualSha256 = downloaded.Sha256;
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(
                    $"SHA256 mismatch for '{destinationFullPath}'. Expected {expectedSha256.ToLowerInvariant()}, got {actualSha256}.");
            }

            _logger.LogInformation("SHA256 verified for {Destination}", destinationRelative);

            stagedExternalFiles.Add(new StagedExternalFile(
                external.Type!, external.Url, destinationRelative, expectedSha256.ToLowerInvariant(), actualSha256));

        var setupFileFullPath = PathSafety.ResolveWithin(stagingDirectory, setupFile, "Package.IntuneWin.SetupFile");
        if (!File.Exists(setupFileFullPath))
        {
            throw new StagingException(
                $"Setup file '{setupFile}' does not exist in the staging directory. " +
                "It must be produced by RepositoryFiles or ExternalFiles.");
        }

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
            SetupFile = setupFile,
            RepositoryFiles = stagedRepositoryFiles,
            ExternalFiles = stagedExternalFiles,
        };
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, SummaryJsonOptions), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Staging summary written to {SummaryPath}", summaryPath);

        return new StagingResult(
            packageIdentifier, platform, architecture, stagingDirectory, setupFile,
            DryRun: false, summaryPath, stagedRepositoryFiles, stagedExternalFiles);
    }

    private StagingResult BuildDryRunResult(
        string packageIdentifier,
        string platform,
        string architecture,
        string stagingDirectory,
        string setupFile,
        List<(string SourceFullPath, string SourceRelative, string DestinationRelative)> repositoryCopies,
        WindowsPackageManifest package)
    {
        foreach (var (_, sourceRelative, destinationRelative) in repositoryCopies)
        {
            _logger.LogInformation("[dry-run] would copy {Source} -> {Destination}", sourceRelative, destinationRelative);
        }

        foreach (var external in package.ExternalFiles)
        {
            _logger.LogInformation(
                "[dry-run] would download {Type} source to {Destination}", external.Type, external.Destination);
        }

        _logger.LogInformation("[dry-run] staging directory would be {StagingDirectory}", stagingDirectory);

        return new StagingResult(
            packageIdentifier, platform, architecture, stagingDirectory, setupFile,
            DryRun: true, SummaryPath: null,
            repositoryCopies.Select(c => new StagedRepositoryFile(c.SourceRelative, c.DestinationRelative)).ToList(),
            package.ExternalFiles
                .Select(e => new StagedExternalFile(e.Type!, e.Url, e.Destination!, e.Sha256?.ToLowerInvariant(), null))
                .ToList());
    }
}
