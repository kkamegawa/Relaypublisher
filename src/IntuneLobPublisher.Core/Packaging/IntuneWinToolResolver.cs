using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Default resolver. Supply-chain protection (doc/00-overview.md 6.15): the tool touches
/// every package payload, so a configured known-good SHA256 is always enforced, and the
/// SHA256 of whatever binary is used is reported for the package metadata.
/// </summary>
public sealed class IntuneWinToolResolver : IIntuneWinToolResolver
{
    /// <summary>Environment variable checked when no command-line tool path is given.</summary>
    public const string ToolPathEnvironmentVariable = "INTUNEWINAPPUTIL_PATH";

    private const string ToolFileName = "IntuneWinAppUtil.exe";

    private readonly IIntuneWinToolDownloader _downloader;
    private readonly ILogger<IntuneWinToolResolver> _logger;
    private readonly Func<string, string?> _environment;

    public IntuneWinToolResolver(IIntuneWinToolDownloader downloader, ILogger<IntuneWinToolResolver> logger)
        : this(downloader, logger, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test constructor allowing environment lookup to be injected.</summary>
    public IntuneWinToolResolver(
        IIntuneWinToolDownloader downloader,
        ILogger<IntuneWinToolResolver> logger,
        Func<string, string?> environment)
    {
        _downloader = downloader;
        _logger = logger;
        _environment = environment;
    }

    public async Task<ResolvedIntuneWinTool> ResolveAsync(IntuneWinToolOptions options, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ExplicitToolPath))
        {
            return await FromLocalFileAsync(
                options.ExplicitToolPath, options, "the --intunewin-tool option", cancellationToken).ConfigureAwait(false);
        }

        var environmentPath = _environment(ToolPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return await FromLocalFileAsync(
                environmentPath, options, $"the {ToolPathEnvironmentVariable} environment variable", cancellationToken)
                .ConfigureAwait(false);
        }

        var version = options.PinnedVersion
            ?? await _downloader.GetLatestVersionAsync(cancellationToken).ConfigureAwait(false);
        var toolPath = Path.Combine(Path.GetFullPath(options.ToolsDirectory), version, ToolFileName);

        if (!File.Exists(toolPath))
        {
            await _downloader.DownloadAsync(version, toolPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Using cached {ToolFileName} {Version} at {Path}", ToolFileName, version, toolPath);
        }

        var sha256 = await ChecksumVerifier.ComputeSha256Async(toolPath, cancellationToken).ConfigureAwait(false);
        if (options.KnownSha256 is not null
            && !string.Equals(sha256, options.KnownSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Never keep a binary that failed verification in the cache.
            File.Delete(toolPath);
            throw new ChecksumMismatchException(
                $"{ToolFileName} {version} does not match the configured known-good SHA256. " +
                $"Expected {options.KnownSha256.ToLowerInvariant()}, got {sha256}. The file was deleted.");
        }

        return new ResolvedIntuneWinTool(toolPath, version, sha256);
    }

    private async Task<ResolvedIntuneWinTool> FromLocalFileAsync(
        string path,
        IntuneWinToolOptions options,
        string origin,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new PackagingException($"IntuneWinAppUtil path '{path}' from {origin} does not exist.");
        }

        var sha256 = await ChecksumVerifier.ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        if (options.KnownSha256 is not null
            && !string.Equals(sha256, options.KnownSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(
                $"{ToolFileName} at '{fullPath}' (from {origin}) does not match the configured known-good SHA256. " +
                $"Expected {options.KnownSha256.ToLowerInvariant()}, got {sha256}.");
        }

        _logger.LogInformation("Using {ToolFileName} from {Origin}: {Path}", ToolFileName, origin, fullPath);
        return new ResolvedIntuneWinTool(fullPath, options.PinnedVersion, sha256);
    }
}
