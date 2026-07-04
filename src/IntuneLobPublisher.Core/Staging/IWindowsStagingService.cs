using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Staging;

/// <summary>Options of a staging run.</summary>
/// <param name="RepositoryRoot">Root directory RepositoryFiles sources and detection scripts resolve against.</param>
/// <param name="OutputDirectory">Directory staged packages are written under.</param>
/// <param name="DryRun">When true only path safety and existence checks run; nothing is copied or downloaded.</param>
public sealed record StagingOptions(string RepositoryRoot, string OutputDirectory, bool DryRun = false);

/// <summary>A repository file copy performed (or planned in dry-run).</summary>
public sealed record StagedRepositoryFile(string Source, string Destination);

/// <summary>An external file download performed (or planned in dry-run).</summary>
public sealed record StagedExternalFile(string Type, string? Url, string Destination, string? ExpectedSha256, string? ActualSha256);

/// <summary>Result of staging one app entry.</summary>
public sealed record StagingResult(
    string PackageIdentifier,
    string Platform,
    string Architecture,
    string StagingDirectory,
    string SetupFile,
    bool DryRun,
    string? SummaryPath,
    IReadOnlyList<StagedRepositoryFile> RepositoryFiles,
    IReadOnlyList<StagedExternalFile> ExternalFiles);

/// <summary>Stages a Windows Win32 app entry into a directory ready for .intunewin generation.</summary>
public interface IWindowsStagingService
{
    Task<StagingResult> StageAsync(
        IntunePackageManifest manifest,
        AppManifest app,
        StagingOptions options,
        CancellationToken cancellationToken);
}
