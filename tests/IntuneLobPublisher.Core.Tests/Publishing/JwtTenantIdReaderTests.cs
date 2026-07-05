using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class JwtTenantIdReaderTests
{
    private static string CreateJwt(object payload)
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.";
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [TestMethod]
    public void ReadTenantId_ValidToken_ReturnsTidClaim()
    {
        var token = CreateJwt(new { tid = "11111111-1111-1111-1111-111111111111", aud = "https://graph.microsoft.com" });

        var tenantId = JwtTenantIdReader.ReadTenantId(token);

        Assert.AreEqual("11111111-1111-1111-1111-111111111111", tenantId);
    }

    [TestMethod]
    public void ReadTenantId_MissingTidClaim_ThrowsFormatException()
    {
        var token = CreateJwt(new { aud = "https://graph.microsoft.com" });

        Assert.ThrowsExactly<FormatException>(() => JwtTenantIdReader.ReadTenantId(token));
    }

    [TestMethod]
    public void ReadTenantId_NotAJwt_ThrowsFormatException()
    {
        Assert.ThrowsExactly<FormatException>(() => JwtTenantIdReader.ReadTenantId("not-a-jwt-token"));
    }

    [TestMethod]
    public void ReadTenantId_InvalidBase64Payload_ThrowsFormatException()
    {
        Assert.ThrowsExactly<FormatException>(() => JwtTenantIdReader.ReadTenantId("header.not!!valid==base64url.signature"));
    }

    [TestMethod]
    public void ReadTenantId_ValidBase64ButNotJsonPayload_ThrowsFormatException()
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes("not-json-content"));
        var token = $"{header}.{body}.";

        Assert.ThrowsExactly<FormatException>(() => JwtTenantIdReader.ReadTenantId(token));
    }
}
