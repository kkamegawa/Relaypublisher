namespace IntuneLobPublisher.Core.Sources;

/// <summary>Configuration for retrying transient source download failures.</summary>
public sealed class SourceRetryOptions
{
    /// <summary>Maximum number of retry attempts, in addition to the initial attempt.</summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>Base delay for exponential backoff.</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound applied to the computed exponential backoff delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
