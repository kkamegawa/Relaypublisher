using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// Checks that DefaultAzureCredential's chain is pinned before publishing (doc/00-overview.md 6.19).
/// Azure.Identity itself reads AZURE_TOKEN_CREDENTIALS (1.15.0+), so nothing needs to be passed to
/// DefaultAzureCredential here - setting the variable is sufficient. When it is unset, the chain is
/// resolved in order and can pick a signed-in IDE or broker identity instead of the intended Azure CLI
/// login, producing a 403 that is indistinguishable from a genuinely missing Graph permission. The
/// wrong identity is usually in the same tenant, so --expected-tenant does not catch it. This is a
/// warning, not an error: a deterministic environment (for example CI with only one credential source
/// available) is a valid configuration even without the variable set.
/// </summary>
internal static class CredentialDeterminismCheck
{
    /// <summary>Environment variable Azure.Identity reads to pin the DefaultAzureCredential chain.</summary>
    public const string EnvironmentVariableName = "AZURE_TOKEN_CREDENTIALS";

    internal const string NotPinnedWarning =
        "AZURE_TOKEN_CREDENTIALS is not set, so DefaultAzureCredential resolves its whole credential chain " +
        "and may authenticate as an identity other than the one you signed in as (a signed-in Visual Studio, " +
        "VS Code or broker identity is tried before the Azure CLI login). That identity is usually in the same " +
        "tenant, so --expected-tenant does not catch it, and the result is a 403 that looks like a missing Graph " +
        "permission. Set AZURE_TOKEN_CREDENTIALS=AzureCliCredential (or another single credential name) to pin it. " +
        "See doc/05-operation.md section 3.";

    /// <summary>
    /// Pure check so it can be tested without mutating the process environment; pass
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> in production code.
    /// </summary>
    public static bool IsCredentialChainPinned(Func<string, string?> environment)
        => !string.IsNullOrWhiteSpace(environment(EnvironmentVariableName));

    /// <summary>Logs <see cref="NotPinnedWarning"/> once when the credential chain is not pinned; does nothing otherwise.</summary>
    public static void WarnIfCredentialChainNotPinned(ILogger logger, Func<string, string?> environment)
    {
        if (IsCredentialChainPinned(environment))
        {
            return;
        }

        logger.LogWarning(NotPinnedWarning);
    }
}
