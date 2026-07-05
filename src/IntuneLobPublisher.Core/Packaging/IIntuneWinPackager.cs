using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>Result of one .intunewin generation.</summary>
/// <param name="IntuneWinSha256">SHA256 of the generated file. Informational only: the file is encrypted with a random key per run, so this hash is not deterministic.</param>
public sealed record IntuneWinPackageResult(
    string PackageIdentifier,
    string Platform,
    string Architecture,
    string IntuneWinPath,
    string IntuneWinSha256,
    string InputHash,
    string? ToolVersion,
    string ToolSha256,
    string MetadataPath);

/// <summary>Generates a .intunewin package from a completed staging run.</summary>
public interface IIntuneWinPackager
{
    Task<IntuneWinPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        StagingResult stagingResult,
        IntuneWinToolOptions toolOptions,
        CancellationToken cancellationToken);
}
