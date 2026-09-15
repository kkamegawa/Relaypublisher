namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Configuration for the Microsoft Graph HTTP pipeline (authentication, tenant guard, retry).</summary>
public sealed class GraphClientOptions
{
    private readonly Uri _baseAddress = new("https://graph.microsoft.com/beta/");

    /// <summary>
    /// Graph base address. Beta unless overridden for testing against a stub server: every Intune app
    /// resource this tool writes (win32LobApp, macOSPkgApp, macOSLobApp) is on Graph beta
    /// (doc/adr/publishing.md 2026-09-10 entry), so there is no longer a per-call v1.0/beta split.
    /// Every client resolves its relative request paths against this value with <see cref="Uri(Uri, string)"/>,
    /// which treats a value without a trailing slash as ending in a file segment and drops it - a value with
    /// no trailing slash is normalized here so a caller-supplied path prefix is never silently lost.
    /// </summary>
    public Uri BaseAddress
    {
        get => _baseAddress;
        init => _baseAddress = value.AbsoluteUri.EndsWith('/')
            ? value
            : new Uri(value.AbsoluteUri + "/", UriKind.Absolute);
    }

    /// <summary>OAuth scope requested from <c>DefaultAzureCredential</c>.</summary>
    public string Scope { get; init; } = "https://graph.microsoft.com/.default";

    /// <summary>When set, the token's `tid` claim must match this value or the first request fails with <see cref="Exceptions.TenantMismatchException"/>.</summary>
    public string? ExpectedTenantId { get; init; }

    /// <summary>Maximum number of retry attempts for 429/503 responses, in addition to the initial attempt.</summary>
    public int MaxRetryAttempts { get; init; } = 5;

    /// <summary>Upper bound applied to both the `Retry-After` value and the computed exponential backoff delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Base delay for exponential backoff when the server does not send `Retry-After`.</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}
