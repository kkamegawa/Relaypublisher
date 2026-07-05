using System.Text.Json;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Reads the `tid` claim from a JWT access token without validating its signature.
/// The token was just issued by Azure AD via <c>DefaultAzureCredential</c>, so signature
/// validation is Azure AD's responsibility here; this only needs the tenant id for the guard check.
/// </summary>
public static class JwtTenantIdReader
{
    /// <summary>Extracts the `tid` claim from a JWT's payload segment.</summary>
    /// <exception cref="FormatException">The token is not a well-formed JWT or has no `tid` claim.</exception>
    public static string ReadTenantId(string accessToken)
    {
        var segments = accessToken.Split('.');
        if (segments.Length < 2)
        {
            throw new FormatException("Access token is not a well-formed JWT (expected at least 2 dot-separated segments).");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64UrlDecode(segments[1]);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Access token payload segment is not valid base64url.", ex);
        }

        using var payload = JsonDocument.Parse(payloadBytes);
        if (!payload.RootElement.TryGetProperty("tid", out var tidElement) || tidElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("Access token payload has no 'tid' claim.");
        }

        return tidElement.GetString()!;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
