using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphWin32LobAppClientTests
{
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);

        public List<(string Method, string Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, body));
            return _responses.Dequeue()(request);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static (GraphWin32LobAppClient Client, QueueHandler Handler) CreateClient(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new GraphWin32LobAppClient(httpClient), handler);
    }

    private static Win32LobAppPayload CreatePayload(string? notes = null) => new()
    {
        DisplayName = "Contoso Tool",
        Description = "Contoso command line tool.",
        Publisher = "Contoso",
        InstallCommandLine = "install.cmd",
        UninstallCommandLine = "uninstall.cmd",
        AllowedArchitectures = "x64",
        MinimumSupportedWindowsRelease = "21H2",
        SetupFilePath = "contoso-tool.exe",
        FileName = "contoso-tool.intunewin",
        InstallExperience = new Win32LobAppInstallExperiencePayload
        {
            RunAsAccount = "system",
            DeviceRestartBehavior = "suppress",
        },
        ReturnCodes = [new Win32LobAppReturnCodePayload { ReturnCode = 0, Type = "success" }],
        Rules =
        [
            new Win32LobAppDetectionRulePayload
            {
                EnforceSignatureCheck = false,
                RunAs32Bit = false,
                ScriptContent = Convert.ToBase64String("exit 0"u8.ToArray()),
            },
        ],
        Notes = notes,
    };

    [TestMethod]
    public async Task CreateAppAsync_PostsWin32LobApp_ReturnsId()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"@odata.type":"#microsoft.graph.win32LobApp","id":"app-1"}"""));

        var id = await client.CreateAppAsync(CreatePayload(notes: """{"packageIdentifier":"Contoso.Tool"}"""), CancellationToken.None);

        Assert.AreEqual("app-1", id);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.win32LobApp\"");
        StringAssert.Contains(body, "\"displayName\":\"Contoso Tool\"");
        StringAssert.Contains(body, "\"notes\":");
        StringAssert.Contains(body, "\"setupFilePath\":\"contoso-tool.exe\"");
        StringAssert.Contains(body, "\"fileName\":\"contoso-tool.intunewin\"");
    }

    [TestMethod]
    public async Task CreateAppAsync_ResponseWithoutId_Throws()
    {
        var (client, _) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"@odata.type":"#microsoft.graph.win32LobApp"}"""));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateAppAsync(CreatePayload(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateAppAsync_Failure_ThrowsWithRequestIds()
    {
        var (client, _) = CreateClient(_ =>
        {
            var response = JsonResponse(HttpStatusCode.BadRequest, """{"error":{"code":"BadRequest"}}""");
            response.Headers.Add("client-request-id", "cr-1");
            response.Headers.Add("request-id", "r-1");
            return response;
        });

        var exception = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateAppAsync(CreatePayload(), CancellationToken.None));

        Assert.AreEqual(400, exception.StatusCode);
        Assert.AreEqual("cr-1", exception.ClientRequestId);
        Assert.AreEqual("r-1", exception.RequestId);
    }

    [TestMethod]
    public async Task UpdateAppAsync_PatchesApp_OmitsNotesWhenNull()
    {
        var (client, handler) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.UpdateAppAsync("app 1", CreatePayload(), CancellationToken.None);

        Assert.AreEqual("PATCH", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app%201", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"displayName\":\"Contoso Tool\"");
        StringAssert.Contains(body, "\"setupFilePath\":\"contoso-tool.exe\"");
        StringAssert.Contains(body, "\"fileName\":\"contoso-tool.intunewin\"");
        Assert.IsFalse(body.Contains("\"notes\""), "Update must omit notes so the content upload flow owns that field.");
    }

    [TestMethod]
    public async Task UpdateAppAsync_Failure_Throws()
    {
        var (client, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var exception = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.UpdateAppAsync("app-1", CreatePayload(), CancellationToken.None));

        Assert.AreEqual(404, exception.StatusCode);
    }
}
