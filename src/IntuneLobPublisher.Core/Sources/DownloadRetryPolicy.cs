using System.Net;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>
/// Retries a whole download operation (request, body copy, file write) on transient failures with
/// capped exponential backoff. Unlike a <see cref="DelegatingHandler"/>, this also covers failures
/// that occur while streaming the response body after the headers were already received.
/// `Retry-After` is intentionally not honored; the capped backoff is a good-enough approximation
/// for source downloads.
/// </summary>
public sealed class DownloadRetryPolicy
{
    private readonly SourceRetryOptions _options;
    private readonly ILogger<DownloadRetryPolicy> _logger;

    public DownloadRetryPolicy(SourceRetryOptions options, ILogger<DownloadRetryPolicy> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs <paramref name="action"/>, retrying transient failures.</summary>
    /// <param name="operationDescription">
    /// Description used in retry log warnings. Callers must redact secrets (query strings, tokens)
    /// before passing it in.
    /// </param>
    public async Task<T> ExecuteAsync<T>(
        string operationDescription,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < _options.MaxRetryAttempts)
            {
                var delay = ComputeBackoffDelay(attempt);
                _logger.LogWarning(
                    "Download '{Operation}' failed transiently (attempt {Attempt}); retrying after {Delay}. {Error}",
                    operationDescription, attempt + 1, delay, ex.Message);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Network-level errors, timeouts, throttling and server errors are worth retrying; client
    /// errors like 401/403/404 indicate configuration problems and fail fast.
    /// </summary>
    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError,
        IOException => true,
        _ => false,
    };

    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        var delay = TimeSpan.FromTicks(_options.BaseRetryDelay.Ticks * (1L << Math.Min(attempt, 20)));
        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }
}
