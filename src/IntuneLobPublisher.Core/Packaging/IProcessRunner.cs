namespace IntuneLobPublisher.Core.Packaging;

/// <summary>Captured result of a finished process.</summary>
public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs an external process and captures stdout/stderr, so packaging is testable without the real tool.</summary>
public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}
