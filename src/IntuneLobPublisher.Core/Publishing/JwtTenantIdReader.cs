using System.Text.Json;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Non-secret identity claims of a Graph access token, read for diagnostic logging only (doc/00-overview.md
/// 6.19). <c>AppId</c> is the calling application's client id (a GUID), <c>IdentityType</c> is `"app"` for
/// an app-only (client-credentials) token, and <c>Roles</c> holds application permission names such as
/// `DeviceManagementApps.ReadWrite.All`. None of these are secrets - the same class of value as the
/// `client-request-id`/`request-id` correlation ids in <see cref="GraphErrorReader"/>. The access token
/// itself is never captured here or logged by any caller.
/// </summary>
public sealed record GraphTokenIdentity(string? AppId, string? IdentityType, IReadOnlyList<string> Roles)
{
    /// <summary>Returned when the token cannot be read; a logging aid must never fail the caller.</summary>
    public static GraphTokenIdentity Unknown { get; } = new(null, null, []);
}

/// <summary>
/// Reads claims from a JWT access token without validating its signature.
/// The token was just issued by Azure AD via <c>DefaultAzureCredential</c>, so signature
/// validation is Azure AD's responsibility here.
/// </summary>
public static class JwtTenantIdReader
{
    /// <summary>Extracts the `tid` claim from a JWT's payload segment.</summary>
    /// <exception cref="FormatException">The token is not a well-formed JWT or has no `tid` claim.</exception>
    public static string ReadTenantId(string accessToken)
    {
        using var payload = ParsePayload(accessToken);

        if (!payload.RootElement.TryGetProperty("tid", out var tidElement) || tidElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("Access token payload has no 'tid' claim.");
        }

        return tidElement.GetString()!;
    }

    /// <summary>
    /// Reads <c>appid</c> (falling back to <c>azp</c> for v2.0-shaped tokens), <c>idtyp</c>, and <c>roles</c>
    /// for diagnostic logging (doc/00-overview.md 6.19: "which identity actually acquired this token").
    /// Unlike <see cref="ReadTenantId"/>, which backs a security-relevant tenant check and must fail loudly,
    /// this never throws - a malformed or claim-less token degrades to <see cref="GraphTokenIdentity.Unknown"/>
    /// rather than breaking the publish that is trying to log it.
    /// </summary>
    public static GraphTokenIdentity ReadIdentity(string accessToken)
    {
        try
        {
            using var payload = ParsePayload(accessToken);
            var root = payload.RootElement;

            var appId = ReadStringClaim(root, "appid") ?? ReadStringClaim(root, "azp");
            var identityType = ReadStringClaim(root, "idtyp");
            var roles = ReadRolesClaim(root);

            return new GraphTokenIdentity(appId, identityType, roles);
        }
        catch (FormatException)
        {
            return GraphTokenIdentity.Unknown;
        }
    }

    private static string? ReadStringClaim(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static IReadOnlyList<string> ReadRolesClaim(JsonElement root)
    {
        if (!root.TryGetProperty("roles", out var rolesElement) || rolesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var roles = new List<string>();
        foreach (var role in rolesElement.EnumerateArray())
        {
            if (role.ValueKind == JsonValueKind.String && role.GetString() is { } value)
            {
                roles.Add(value);
            }
        }

        return roles;
    }

    /// <summary>Decodes and parses a JWT's payload (second dot-separated segment).</summary>
    /// <exception cref="FormatException">The token is not a well-formed JWT or its payload is not valid JSON.</exception>
    private static JsonDocument ParsePayload(string accessToken)
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

        try
        {
            return JsonDocument.Parse(payloadBytes);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Access token payload segment is not valid JSON.", ex);
        }
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
