using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Details of a failed Microsoft Graph response, already rendered into a message that is actionable on
/// its own. Produced by <see cref="GraphErrorReader.ReadFailureAsync"/>; callers pick the exception type
/// because the same status code means different things depending on which call failed.
/// </summary>
/// <param name="Summary">Human-readable one-line description, including correlation ids.</param>
/// <param name="StatusCode">HTTP status code of the failing response.</param>
/// <param name="ClientRequestId">The `client-request-id` header Graph echoes back. Never a secret.</param>
/// <param name="RequestId">The `request-id` header Graph returns. Never a secret.</param>
/// <param name="ErrorCode">Graph's `error.code` value, when the body carried one.</param>
public sealed record GraphFailure(
    string Summary,
    int StatusCode,
    string? ClientRequestId,
    string? RequestId,
    string? ErrorCode)
{
    /// <summary>Builds the per-call exception. <paramref name="prefix"/> names the operation that failed.</summary>
    public GraphRequestException ToRequestException(string? prefix = null)
        => new(Compose(prefix), StatusCode, ClientRequestId, RequestId, ErrorCode);

    /// <summary>Builds the identity-wide exception used when no other call can succeed either.</summary>
    public GraphAccessDeniedException ToAccessDeniedException(string? prefix = null)
        => new(Compose(prefix), StatusCode, ClientRequestId, RequestId, ErrorCode);

    private string Compose(string? prefix)
        => string.IsNullOrEmpty(prefix) ? Summary : $"{prefix} {Summary}";
}

/// <summary>
/// Turns a failed Graph response into a message an operator can act on without reading the logs:
/// Graph's own `error.code` / `error.message`, the `client-request-id` / `request-id` correlation
/// headers, and - for 401/403 - the app-registration mistake that causes almost all of them.
/// Without this, callers only reported the HTTP status, which cannot distinguish "the app registration
/// is missing the permission" from "the tenant has no Intune license" from "beta is unavailable".
/// Only the fields listed above are copied into the message; response headers are never dumped wholesale
/// and the request's Authorization header is never touched (AGENTS.md secrets rule).
/// </summary>
public static class GraphErrorReader
{
    /// <summary>Graph error bodies are a few hundred bytes; cap the read so a huge body cannot be pulled into memory.</summary>
    private const int MaxErrorBodyBytes = 8 * 1024;

    /// <summary>Keeps a verbose server message from swamping the CLI's single-line error output.</summary>
    private const int MaxErrorMessageLength = 500;

    /// <summary>
    /// Named after the actual failure mode seen in practice: the permission is registered as a
    /// *delegated* permission, which a client-credentials token never carries, so Graph answers 403
    /// even though the portal shows the permission as granted and consented.
    /// </summary>
    private const string PermissionHint =
        "Grant 'DeviceManagementApps.ReadWrite.All' as an application permission (not a delegated one) " +
        "on the app registration, grant admin consent, then acquire a new token: an app-only token carries " +
        "application permissions in its 'roles' claim and never carries delegated ones. The tenant also " +
        "needs an active Intune license. See doc/05-operation.md section 1.";

    /// <summary>
    /// Reads a failed response. Never throws: a missing, truncated, non-JSON or unparseable body just
    /// degrades the message back to the status code, because the caller is already on a failure path.
    /// </summary>
    public static async Task<GraphFailure> ReadFailureAsync(
        HttpResponseMessage response,
        string requestUri,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        var (errorCode, errorMessage) = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
        var clientRequestId = GetHeader(response, "client-request-id");
        var requestId = GetHeader(response, "request-id");

        var summary = new StringBuilder()
            .Append("Graph request to '").Append(requestUri).Append("' returned ").Append(statusCode);
        if (errorCode is not null)
        {
            summary.Append(" (").Append(errorCode).Append(')');
        }

        summary.Append('.');
        if (errorMessage is not null)
        {
            summary.Append(' ').Append(errorMessage);
        }

        if (clientRequestId is not null || requestId is not null)
        {
            summary.Append(" [client-request-id=").Append(clientRequestId ?? "(none)")
                .Append(", request-id=").Append(requestId ?? "(none)").Append(']');
        }

        if (statusCode is 401 or 403)
        {
            summary.Append(' ').Append(PermissionHint);
        }

        return new GraphFailure(summary.ToString(), statusCode, clientRequestId, requestId, errorCode);
    }

    /// <summary>Reads a response header Graph uses for support correlation. Returns null when absent.</summary>
    public static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<(string? Code, string? Message)> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBodyBytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return (null, null);
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadStringProperty(error, "code"), ReadStringProperty(error, "message"));
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or
                                   ObjectDisposedException or NotSupportedException)
        {
            return (null, null);
        }
    }

    private static string? ReadStringProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Graph messages are sometimes multi-line; keep the CLI's error output to one line.
        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > MaxErrorMessageLength
            ? string.Concat(collapsed.AsSpan(0, MaxErrorMessageLength), "...")
            : collapsed;
    }
}
