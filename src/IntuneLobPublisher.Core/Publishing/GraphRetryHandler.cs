using System.Net;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Retries Microsoft Graph calls on 429 (throttled) and 503 (unavailable), honoring `Retry-After`
/// when present and falling back to a capped exponential backoff otherwise. Buffers request content
/// so it can be resent on every attempt.
/// </summary>
public sealed class GraphRetryHandler : DelegatingHandler
{
    private readonly GraphClientOptions _options;
    private readonly ILogger<GraphRetryHandler> _logger;

    public GraphRetryHandler(GraphClientOptions options, ILogger<GraphRetryHandler> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var contentBytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = CloneRequest(request, contentBytes);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetryAttempts)
            {
                _logger.LogWarning(ex, "Graph request to {Uri} failed transiently (attempt {Attempt}); retrying.", request.RequestUri, attempt + 1);
                await Task.Delay(ComputeBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new GraphRequestException($"Graph request to '{request.RequestUri}' failed after {attempt} retries.", ex);
            }

            var isThrottled = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            if (isThrottled && attempt < _options.MaxRetryAttempts)
            {
                var delay = GetRetryAfterDelay(response) ?? ComputeBackoffDelay(attempt);
                _logger.LogWarning(
                    "Graph request to {Uri} was throttled with {StatusCode} (attempt {Attempt}); retrying after {Delay}.",
                    request.RequestUri, (int)response.StatusCode, attempt + 1, delay);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var clientRequestId = GetHeader(response, "client-request-id");
                var requestId = GetHeader(response, "request-id");
                _logger.LogError(
                    "Graph request to {Uri} failed with {StatusCode}. client-request-id={ClientRequestId} request-id={RequestId}",
                    request.RequestUri, (int)response.StatusCode, clientRequestId, requestId);

                if (isThrottled)
                {
                    // Retries exhausted: a raw 429/503 is never a meaningful result for a caller to branch on.
                    var statusCode = (int)response.StatusCode;
                    var uri = request.RequestUri;
                    response.Dispose();
                    throw new GraphRequestException(
                        $"Graph request to '{uri}' was still throttled with {statusCode} after {attempt} retries.",
                        statusCode, clientRequestId, requestId);
                }
            }

            return response;
        }
    }

    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        var delay = TimeSpan.FromTicks(_options.BaseRetryDelay.Ticks * (1L << Math.Min(attempt, 20)));
        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    private TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        var delay = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
        if (delay is null || delay <= TimeSpan.Zero)
        {
            return null;
        }

        return delay.Value > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay.Value;
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (contentBytes is not null)
        {
            var content = new ByteArrayContent(contentBytes);
            if (original.Content is not null)
            {
                foreach (var header in original.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            clone.Content = content;
        }

        return clone;
    }
}
