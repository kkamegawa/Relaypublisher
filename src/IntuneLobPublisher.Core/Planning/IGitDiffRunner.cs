using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Planning;

/// <summary>Lists files changed since a git base ref.</summary>
public interface IGitDiffRunner
{
    /// <summary>
    /// Returns repo-root-relative changed file paths, or null when the base ref
    /// cannot be resolved (unknown sha, zero sha, not a git repository).
    /// </summary>
    Task<IReadOnlyList<string>?> GetChangedFilesAsync(
        string repositoryRoot,
        string baseRef,
        CancellationToken cancellationToken);
}

/// <summary>Runs `git diff --name-only` as a child process.</summary>
public sealed class GitDiffRunner : IGitDiffRunner
{
    private const string ZeroSha = "0000000000000000000000000000000000000000";

    private readonly ILogger<GitDiffRunner> _logger;

    public GitDiffRunner(ILogger<GitDiffRunner> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> GetChangedFilesAsync(
        string repositoryRoot,
        string baseRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseRef) || baseRef == ZeroSha)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--name-only");
        startInfo.ArgumentList.Add(baseRef);
        startInfo.ArgumentList.Add("HEAD");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "git diff against base ref '{BaseRef}' failed (exit code {ExitCode}): {Error}",
                baseRef, process.ExitCode, error.Trim());
            return null;
        }

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
