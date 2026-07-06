using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Runs IntuneWinAppUtil.exe over a staged directory and writes package-metadata.json with
/// the deterministic input hash, the tool version/SHA256 and the generated file's SHA256.
/// Tool output never contains secrets, so stdout/stderr are safe to log and to include in errors.
/// </summary>
public sealed class IntuneWinPackager : IIntuneWinPackager
{
    private readonly IIntuneWinToolResolver _toolResolver;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<IntuneWinPackager> _logger;

    public IntuneWinPackager(
        IIntuneWinToolResolver toolResolver,
        IProcessRunner processRunner,
        ILogger<IntuneWinPackager> logger)
    {
        _toolResolver = toolResolver;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<IntuneWinPackageResult> CreatePackageAsync(
        IntunePackageManifest manifest,
        StagingResult stagingResult,
        IntuneWinToolOptions toolOptions,
        CancellationToken cancellationToken)
    {
        if (stagingResult.DryRun)
        {
            throw new PackagingException("Cannot generate .intunewin from a dry-run staging result.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PackagingException(
                ".intunewin generation requires Windows because IntuneWinAppUtil.exe is a Windows executable.");
        }

        var stagingDirectory = Path.GetFullPath(stagingResult.StagingDirectory);
        if (!Directory.Exists(stagingDirectory))
        {
            throw new PackagingException($"Staging directory '{stagingDirectory}' does not exist.");
        }

        var setupFileFullPath = PathSafety.ResolveWithin(
            stagingDirectory, stagingResult.SetupFile, "Package.IntuneWin.SetupFile");
        if (!File.Exists(setupFileFullPath))
        {
            throw new PackagingException(
                $"Setup file '{stagingResult.SetupFile}' does not exist in staging directory '{stagingDirectory}'.");
        }

        var tool = await _toolResolver.ResolveAsync(toolOptions, cancellationToken).ConfigureAwait(false);

        // The .intunewin and its metadata go next to the staging directory
        // (<output>/<PackageIdentifier>/<platform>-<architecture>/).
        var outputDirectory = Path.GetDirectoryName(stagingDirectory)!;

        _logger.LogInformation(
            "Generating .intunewin for {PackageIdentifier} {Platform}-{Architecture} with tool version {ToolVersion}",
            stagingResult.PackageIdentifier, stagingResult.Platform, stagingResult.Architecture,
            tool.Version ?? "(unpinned local)");

        var run = await _processRunner.RunAsync(
            tool.Path,
            ["-c", stagingDirectory, "-s", setupFileFullPath, "-o", outputDirectory, "-q"],
            outputDirectory,
            cancellationToken).ConfigureAwait(false);

        if (run.StandardOutput.Length > 0)
        {
            _logger.LogDebug("IntuneWinAppUtil stdout: {StandardOutput}", run.StandardOutput);
        }

        if (run.StandardError.Length > 0)
        {
            _logger.LogWarning("IntuneWinAppUtil stderr: {StandardError}", run.StandardError);
        }

        if (run.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(run.StandardError) ? run.StandardOutput : run.StandardError;
            throw new PackagingException(
                $"IntuneWinAppUtil.exe exited with code {run.ExitCode} for '{stagingResult.PackageIdentifier}'. Output: {detail.Trim()}");
        }

        // The tool names the package after the setup file's base name.
        var intuneWinFileName = Path.GetFileNameWithoutExtension(stagingResult.SetupFile) + ".intunewin";
        var intuneWinPath = Path.Combine(outputDirectory, intuneWinFileName);
        if (!File.Exists(intuneWinPath))
        {
            throw new PackagingException(
                $"IntuneWinAppUtil.exe reported success but '{intuneWinPath}' was not created. Output: {run.StandardOutput.Trim()}");
        }

        var inputHash = await InputHashCalculator.ComputeInputHashAsync(manifest, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        var intuneWinSha256 = await ChecksumVerifier.ComputeSha256Async(intuneWinPath, cancellationToken)
            .ConfigureAwait(false);

        var metadataPath = Path.Combine(outputDirectory, PackageMetadataJson.FileName);
        var metadata = new PackageMetadata(
            stagingResult.PackageIdentifier,
            manifest.PackageVersion,
            stagingResult.Platform,
            stagingResult.Architecture,
            inputHash,
            new PackageToolMetadata("IntuneWinAppUtil.exe", tool.Version, tool.Sha256),
            intuneWinFileName,
            // Informational only; a random encryption key makes this hash non-deterministic.
            intuneWinSha256,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, PackageMetadataJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Package metadata written to {MetadataPath}", metadataPath);

        return new IntuneWinPackageResult(
            stagingResult.PackageIdentifier,
            stagingResult.Platform,
            stagingResult.Architecture,
            intuneWinPath,
            intuneWinSha256,
            inputHash,
            tool.Version,
            tool.Sha256,
            metadataPath);
    }
}
