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

    [TestMethod]
    public void ReadIdentity_TokenWithAllClaims_ReturnsAppIdIdentityTypeAndRoles()
    {
        var token = CreateJwt(new
        {
            tid = "11111111-1111-1111-1111-111111111111",
            appid = "22222222-2222-2222-2222-222222222222",
            idtyp = "app",
            roles = new[] { "DeviceManagementApps.ReadWrite.All" },
        });

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.AreEqual("22222222-2222-2222-2222-222222222222", identity.AppId);
        Assert.AreEqual("app", identity.IdentityType);
        Assert.HasCount(1, identity.Roles);
        Assert.AreEqual("DeviceManagementApps.ReadWrite.All", identity.Roles[0]);
    }

    [TestMethod]
    public void ReadIdentity_MultipleRoles_ReturnsAllRolesInOrder()
    {
        var token = CreateJwt(new { roles = new[] { "DeviceManagementApps.ReadWrite.All", "User.Read.All" } });

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.HasCount(2, identity.Roles);
        Assert.AreEqual("DeviceManagementApps.ReadWrite.All", identity.Roles[0]);
        Assert.AreEqual("User.Read.All", identity.Roles[1]);
    }

    [TestMethod]
    public void ReadIdentity_AppIdMissingButAzpPresent_FallsBackToAzp()
    {
        var token = CreateJwt(new { azp = "33333333-3333-3333-3333-333333333333" });

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.AreEqual("33333333-3333-3333-3333-333333333333", identity.AppId);
    }

    [TestMethod]
    public void ReadIdentity_NoIdentityClaims_ReturnsUnknownWithoutThrowing()
    {
        var token = CreateJwt(new { tid = "11111111-1111-1111-1111-111111111111" });

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.IsNull(identity.AppId);
        Assert.IsNull(identity.IdentityType);
        Assert.IsEmpty(identity.Roles);
    }

    [TestMethod]
    public void ReadIdentity_RolesClaimIsNotAnArray_ReturnsEmptyRoles()
    {
        var token = CreateJwt(new { roles = "not-an-array" });

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.IsEmpty(identity.Roles);
    }

    [TestMethod]
    public void ReadIdentity_NotAJwt_ReturnsUnknownWithoutThrowing()
    {
        var identity = JwtTenantIdReader.ReadIdentity("not-a-jwt-token");

        Assert.AreSame(GraphTokenIdentity.Unknown, identity);
    }

    [TestMethod]
    public void ReadIdentity_ValidBase64ButNotJsonPayload_ReturnsUnknownWithoutThrowing()
    {
        var header = Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes("not-json-content"));
        var token = $"{header}.{body}.";

        var identity = JwtTenantIdReader.ReadIdentity(token);

        Assert.AreSame(GraphTokenIdentity.Unknown, identity);
    }
}
