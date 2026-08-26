namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// What happened when a batch's semantic PKG inspection warnings were judged against
/// <c>--force</c> and, on a TTY, an interactive confirmation (doc/00-overview.md 6.21, issue #116).
/// </summary>
internal enum WarningGateDecision
{
    /// <summary>No warnings were raised; nothing to acknowledge.</summary>
    NoWarnings,

    /// <summary>A TTY operator answered <c>y</c> to the confirmation.</summary>
    Acknowledged,

    /// <summary><c>--force</c> acknowledged the warnings without prompting.</summary>
    ForceAcknowledged,

    /// <summary>A TTY operator answered anything other than <c>y</c> (including EOF/empty input).</summary>
    Declined,

    /// <summary>Non-interactive and no <c>--force</c> was supplied; the caller must fail closed.</summary>
    ForceRequired,
}

/// <summary>
/// Decides whether a batch of semantic macOS PKG inspection warnings may be acknowledged, following
/// doc/05-operation.md's table: no warnings always proceeds; on a TTY a single <c>[y/N]</c> confirmation
/// covers every warning in the batch; off a TTY, <c>--force</c> is required. The decision is a pure
/// function of already-known truth values so it is fully unit-testable without a real console - the
/// thin wrappers below (<see cref="IsInteractive"/>, <see cref="ConfirmOnConsole"/>) are the only parts
/// that touch <see cref="Console"/>, matching the shape of <see cref="CredentialDeterminismCheck"/>.
/// </summary>
internal static class SemanticWarningGate
{
    internal static WarningGateDecision Decide(bool hasWarnings, bool force, bool interactive, Func<bool> confirm)
    {
        if (!hasWarnings)
        {
            return WarningGateDecision.NoWarnings;
        }

        if (force)
        {
            return WarningGateDecision.ForceAcknowledged;
        }

        if (!interactive)
        {
            return WarningGateDecision.ForceRequired;
        }

        return confirm() ? WarningGateDecision.Acknowledged : WarningGateDecision.Declined;
    }

    /// <summary>
    /// True only when both standard input and standard output are an interactive terminal: a prompt
    /// written to a redirected stdout would never be seen, and a redirected stdin can never answer it.
    /// </summary>
    internal static bool IsInteractive(Func<bool> inputRedirected, Func<bool> outputRedirected)
        => !inputRedirected() && !outputRedirected();

    /// <summary>
    /// Reads a single <c>[y/N]</c> answer. EOF, an empty line, or anything other than a case-insensitive
    /// "y" is treated as "no" - the default is to stop, matching doc/05-operation.md.
    /// </summary>
    internal static bool ConfirmOnConsole(TextReader input, TextWriter output)
    {
        output.Write("Proceed with these semantic warnings? [y/N] ");
        var line = input.ReadLine();
        return string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
}
