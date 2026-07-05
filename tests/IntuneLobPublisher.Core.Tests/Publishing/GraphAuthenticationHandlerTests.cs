using System.Net;
using Azure.Core;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphAuthenticationHandlerTests
{
    private const string TenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly Func<AccessToken> _tokenFactory;

        public StubTokenCredential(Func<AccessToken> tokenFactory) => _tokenFactory = tokenFactory;

        public int CallCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return _tokenFactory();
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return new ValueTask<AccessToken>(_tokenFactory());
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static string CreateFakeAccessToken(string tenantId)
    {
        static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Base64UrlEncode("{\"alg\":\"none\"}"u8.ToArray());
        var payload = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes($$"""{"tid":"{{tenantId}}"}"""));
        return $"{header}.{payload}.";
    }

    private static (HttpClient Client, RecordingHandler Inner, StubTokenCredential Credential) CreateClient(
        GraphClientOptions options, Func<AccessToken> tokenFactory)
    {
        var credential = new StubTokenCredential(tokenFactory);
        var inner = new RecordingHandler();
        var handler = new GraphAuthenticationHandler(credential, options) { InnerHandler = inner };
        return (new HttpClient(handler), inner, credential);
    }

    [TestMethod]
    public async Task SendAsync_NoExpectedTenant_AttachesBearerTokenAndSucceeds()
    {
        var (client, inner, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddHours(1)));

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Bearer", inner.Requests[0].Headers.Authorization!.Scheme);
        Assert.IsNotNull(inner.Requests[0].Headers.Authorization!.Parameter);
    }

    [TestMethod]
    public async Task SendAsync_ExpectedTenantMatches_Succeeds()
    {
        var (client, inner, _) = CreateClient(
            new GraphClientOptions { ExpectedTenantId = TenantA },
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddHours(1)));

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.HasCount(1, inner.Requests);
    }

    [TestMethod]
    public async Task SendAsync_ExpectedTenantMismatch_ThrowsBeforeSendingRequest()
    {
        var (client, inner, _) = CreateClient(
            new GraphClientOptions { ExpectedTenantId = TenantA },
            () => new AccessToken(CreateFakeAccessToken(TenantB), DateTimeOffset.UtcNow.AddHours(1)));

        var ex = await Assert.ThrowsExactlyAsync<TenantMismatchException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps"));

        Assert.AreEqual(TenantA, ex.ExpectedTenantId);
        Assert.AreEqual(TenantB, ex.ActualTenantId);
        Assert.IsEmpty(inner.Requests);
    }

    [TestMethod]
    public async Task SendAsync_ValidToken_IsCachedAcrossCalls()
    {
        var (client, _, credential) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddHours(1)));

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");
        await client.GetAsync("https://graph.microsoft.com/v1.0/b");

        Assert.AreEqual(1, credential.CallCount);
    }

    [TestMethod]
    public async Task SendAsync_ExpiringToken_RefetchesOnNextCall()
    {
        var (client, _, credential) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddMinutes(1)));

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");
        await client.GetAsync("https://graph.microsoft.com/v1.0/b");

        Assert.AreEqual(2, credential.CallCount);
    }
}
