using System.Net;
using System.Text.Json;
using Azure.Core;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphAuthenticationHandlerTests
{
    private const string TenantA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TenantB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string AppId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

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

    /// <summary>Captures formatted log messages so tests can assert on identity logging.</summary>
    private sealed class CapturingLogger : ILogger<GraphAuthenticationHandler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static string CreateFakeAccessToken(
        string tenantId, string? appId = null, string? idtyp = null, string[]? roles = null)
    {
        static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Base64UrlEncode("{\"alg\":\"none\"}"u8.ToArray());
        var claims = new Dictionary<string, object?> { ["tid"] = tenantId };
        if (appId is not null) { claims["appid"] = appId; }
        if (idtyp is not null) { claims["idtyp"] = idtyp; }
        if (roles is not null) { claims["roles"] = roles; }
        var payload = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
        return $"{header}.{payload}.";
    }

    private static (HttpClient Client, RecordingHandler Inner, StubTokenCredential Credential) CreateClient(
        GraphClientOptions options, Func<AccessToken> tokenFactory, ILogger<GraphAuthenticationHandler>? logger = null)
    {
        var credential = new StubTokenCredential(tokenFactory);
        var inner = new RecordingHandler();
        var handler = new GraphAuthenticationHandler(credential, options, logger ?? NullLogger<GraphAuthenticationHandler>.Instance)
        {
            InnerHandler = inner,
        };
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
    public async Task SendAsync_RequestTargetsUnexpectedHost_ThrowsWithoutFetchingTokenOrSendingRequest()
    {
        var (client, inner, credential) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddHours(1)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetAsync("https://evil.example.com/steal-token"));

        Assert.AreEqual(0, credential.CallCount);
        Assert.IsEmpty(inner.Requests);
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

    [TestMethod]
    public async Task SendAsync_FreshToken_LogsAcquiredIdentity()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(
                CreateFakeAccessToken(TenantA, AppId, "app", ["DeviceManagementApps.ReadWrite.All"]),
                DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");

        Assert.HasCount(1, logger.Messages);
        StringAssert.Contains(logger.Messages[0], AppId);
        StringAssert.Contains(logger.Messages[0], "idtyp=app");
        StringAssert.Contains(logger.Messages[0], "DeviceManagementApps.ReadWrite.All");
    }

    [TestMethod]
    public async Task SendAsync_FreshToken_NeverLogsTheAccessToken()
    {
        var logger = new CapturingLogger();
        var accessToken = CreateFakeAccessToken(TenantA, AppId, "app", ["DeviceManagementApps.ReadWrite.All"]);
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(accessToken, DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");

        Assert.IsTrue(logger.Messages.TrueForAll(message => !message.Contains(accessToken, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SendAsync_NoExpectedTenant_StillLogsAcquiredIdentity()
    {
        // The identity matters most exactly when no tenant is pinned, so this must not depend on
        // VerifyTenant's early return for ExpectedTenantId being unset.
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA, AppId, "app"), DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");

        Assert.HasCount(1, logger.Messages);
        StringAssert.Contains(logger.Messages[0], AppId);
    }

    [TestMethod]
    public async Task SendAsync_CachedToken_LogsIdentityOnlyOnce()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA, AppId, "app"), DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");
        await client.GetAsync("https://graph.microsoft.com/v1.0/b");

        Assert.HasCount(1, logger.Messages);
    }

    [TestMethod]
    public async Task SendAsync_ExpiringToken_LogsIdentityPerAcquisition()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA, AppId, "app"), DateTimeOffset.UtcNow.AddMinutes(1)),
            logger);

        await client.GetAsync("https://graph.microsoft.com/v1.0/a");
        await client.GetAsync("https://graph.microsoft.com/v1.0/b");

        Assert.HasCount(2, logger.Messages);
    }

    [TestMethod]
    public async Task SendAsync_TokenWithoutIdentityClaims_LogsPlaceholdersAndSucceeds()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions(),
            () => new AccessToken(CreateFakeAccessToken(TenantA), DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/a");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.HasCount(1, logger.Messages);
        StringAssert.Contains(logger.Messages[0], "(none)");
    }

    [TestMethod]
    public async Task SendAsync_ExpectedTenantMismatch_LogsIdentityBeforeThrowing()
    {
        var logger = new CapturingLogger();
        var (client, _, _) = CreateClient(
            new GraphClientOptions { ExpectedTenantId = TenantA },
            () => new AccessToken(CreateFakeAccessToken(TenantB, AppId, "app"), DateTimeOffset.UtcNow.AddHours(1)),
            logger);

        await Assert.ThrowsExactlyAsync<TenantMismatchException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/a"));

        Assert.HasCount(1, logger.Messages);
        StringAssert.Contains(logger.Messages[0], AppId);
    }
}
