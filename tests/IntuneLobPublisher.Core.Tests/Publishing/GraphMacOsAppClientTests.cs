using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphMacOsAppClientTests
{
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);

        public List<(string Method, string Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return _responses.Dequeue()(request);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static (GraphMacOsAppClient Client, QueueHandler Handler) CreateClient(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new GraphMacOsAppClient(httpClient), handler);
    }

    private static MacOsPkgAppPayload PkgPayload() => new()
    {
        DisplayName = "Contoso Tool [macOS Arm64]",
        Description = "Internal tool.",
        Publisher = "Contoso Ltd.",
        FileName = "contoso-tool-arm64.pkg",
        MinimumSupportedOperatingSystem = new MacOsMinimumOperatingSystemPayload { V14_0 = true },
        PrimaryBundleId = "com.contoso.tool",
        PrimaryBundleVersion = "1.2.3",
        IncludedApps = [new MacOsIncludedAppPayload { BundleId = "com.contoso.tool", BundleVersion = "1.2.3" }],
    };

    private static MacOsLobAppPayload LobPayload() => new()
    {
        DisplayName = "Contoso Tool [macOS Arm64]",
        Description = "Internal tool.",
        Publisher = "Contoso Ltd.",
        FileName = "contoso-tool-arm64.pkg",
        MinimumSupportedOperatingSystem = new MacOsMinimumOperatingSystemPayload { V13_0 = true },
        BuildNumber = "1.2.3",
        VersionNumber = "1.2.3",
        ChildApps = [new MacOsLobChildAppPayload { BundleId = "com.contoso.tool", BuildNumber = "1.2.3", VersionNumber = "1.2.3" }],
    };

    [TestMethod]
    public async Task CreateAppAsync_Pkg_PostsToBetaWithMacOsPkgAppODataType()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"app-1"}"""));

        var id = await client.CreateAppAsync(PkgPayload(), useBeta: true, CancellationToken.None);

        Assert.AreEqual("app-1", id);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.macOSPkgApp\"");
        StringAssert.Contains(body, "\"primaryBundleId\":\"com.contoso.tool\"");
        StringAssert.Contains(body, "\"includedApps\"");
    }

    [TestMethod]
    public async Task CreateAppAsync_PkgWithScripts_IncludesMacOsAppScriptProperties()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"app-1"}"""));
        var payload = new MacOsPkgAppPayload
        {
            DisplayName = "Contoso Tool [macOS Arm64]",
            Description = "Internal tool.",
            Publisher = "Contoso Ltd.",
            FileName = "contoso-tool-arm64.pkg",
            MinimumSupportedOperatingSystem = new MacOsMinimumOperatingSystemPayload { V14_0 = true },
            PrimaryBundleId = "com.contoso.tool",
            PrimaryBundleVersion = "1.2.3",
            IncludedApps = [new MacOsIncludedAppPayload { BundleId = "com.contoso.tool", BundleVersion = "1.2.3" }],
            PreInstallScript = new MacOsAppScriptPayload { ScriptContent = "IyEvYmluL2Jhc2g=" },
            PostInstallScript = new MacOsAppScriptPayload { ScriptContent = "IyEvYmluL2Jhc2gK" },
        };

        await client.CreateAppAsync(payload, useBeta: true, CancellationToken.None);

        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"preInstallScript\":{\"@odata.type\":\"#microsoft.graph.macOSAppScript\",\"scriptContent\":\"IyEvYmluL2Jhc2g=\"}");
        StringAssert.Contains(body, "\"postInstallScript\":{\"@odata.type\":\"#microsoft.graph.macOSAppScript\",\"scriptContent\":\"IyEvYmluL2Jhc2gK\"}");
    }

    [TestMethod]
    public async Task CreateAppAsync_PkgWithoutScripts_OmitsScriptProperties()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"app-1"}"""));

        await client.CreateAppAsync(PkgPayload(), useBeta: true, CancellationToken.None);

        var body = handler.Requests[0].Body!;
        Assert.IsFalse(body.Contains("preInstallScript", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("postInstallScript", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreateAppAsync_Lob_PostsToV1WithMacOsLobAppODataTypeAndChildApps()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"app-2"}"""));

        var id = await client.CreateAppAsync(LobPayload(), useBeta: false, CancellationToken.None);

        Assert.AreEqual("app-2", id);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.macOSLobApp\"");
        StringAssert.Contains(body, "\"childApps\"");
        StringAssert.Contains(body, "\"buildNumber\":\"1.2.3\"");
        Assert.IsFalse(body.Contains("primaryBundleId", StringComparison.Ordinal), "The lob payload must not leak pkg-only properties.");
    }

    [TestMethod]
    public async Task UpdateAppAsync_Pkg_PatchesBeta()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.UpdateAppAsync("app-1", PkgPayload(), useBeta: true, CancellationToken.None);

        Assert.AreEqual("PATCH", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1", handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task UpdateAppAsync_Lob_PatchesV1()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await client.UpdateAppAsync("app-2", LobPayload(), useBeta: false, CancellationToken.None);

        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-2", handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task CreateAppAsync_ErrorResponse_ThrowsGraphRequestExceptionWithRequestIds()
    {
        var (client, _) = CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("client-request-id", "client-id-1");
            response.Headers.TryAddWithoutValidation("request-id", "request-id-1");
            return response;
        });

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateAppAsync(PkgPayload(), useBeta: true, CancellationToken.None));

        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("client-id-1", ex.ClientRequestId);
        Assert.AreEqual("request-id-1", ex.RequestId);
    }

    [TestMethod]
    public async Task CreateAppAsync_CreatedWithoutId_Throws()
    {
        var (client, _) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, "{}"));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateAppAsync(PkgPayload(), useBeta: true, CancellationToken.None));
    }
}
