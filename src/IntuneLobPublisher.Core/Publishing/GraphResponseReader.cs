using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The response handling every Graph client needs: fail with an actionable message on a non-success
/// status (via <see cref="GraphErrorReader"/>), and deserialize a success body without letting a raw
/// <see cref="JsonException"/> escape. Each client used to carry its own byte-identical copy of this,
/// which meant an improvement to the error message had to be repeated four times.
/// </summary>
public static class GraphResponseReader
{
    /// <summary>Throws a <see cref="GraphRequestException"/> when the response is not a success status.</summary>
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var failure = await GraphErrorReader
            .ReadFailureAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
        throw failure.ToRequestException();
    }

    /// <summary>
    /// Ensures success, then deserializes the body. A malformed or empty body is reported as a
    /// <see cref="GraphRequestException"/> rather than a <see cref="JsonException"/> so callers only
    /// have to handle one exception family.
    /// </summary>
    public static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        T? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw BodyFailure(response, $"Graph returned a malformed body for '{requestUri}'.");
        }

        return body ?? throw BodyFailure(response, $"Graph returned an empty body for '{requestUri}'.");
    }

    /// <summary>
    /// Builds the exception for a success response whose body is unusable. Kept here so the correlation
    /// ids are attached the same way as on the failure path.
    /// </summary>
    public static GraphRequestException BodyFailure(HttpResponseMessage response, string message)
        => new(
            message,
            (int)response.StatusCode,
            GraphErrorReader.GetHeader(response, "client-request-id"),
            GraphErrorReader.GetHeader(response, "request-id"));
}
