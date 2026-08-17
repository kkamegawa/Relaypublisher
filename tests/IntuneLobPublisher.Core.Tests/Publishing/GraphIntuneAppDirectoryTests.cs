using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphIntuneAppDirectoryTests
{
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);

        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!.ToString());
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpClient CreateClient(QueueHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"),
    };

    [TestMethod]
    public async Task ListAppsAsync_SinglePage_ReturnsAllApps()
    {
        var handler = new QueueHandler(_ => JsonResponse(
            """{"value":[{"id":"app-1","displayName":"Contoso Tool [Windows x64]","notes":"{}"},{"id":"app-2","displayName":"Other App","notes":null}]}"""));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var apps = await directory.ListAppsAsync(CancellationToken.None);

        Assert.HasCount(2, apps);
        Assert.AreEqual("app-1", apps[0].Id);
        Assert.AreEqual("Contoso Tool [Windows x64]", apps[0].DisplayName);
        Assert.AreEqual("app-2", apps[1].Id);
        Assert.IsNull(apps[1].Notes);
    }

    [TestMethod]
    public async Task ListAppsAsync_ListsViaBetaSoMacOsPkgAppsAreNotOmitted()
    {
        // macOSPkgApp is beta-only (https://learn.microsoft.com/graph/api/resources/intune-apps-macospkgapp),
        // so listing via v1.0 could silently miss pkg apps during app resolution.
        var handler = new QueueHandler(_ => JsonResponse("""{"value":[]}"""));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        await directory.ListAppsAsync(CancellationToken.None);

        Assert.AreEqual(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$select=id,displayName,notes",
            handler.RequestedUris[0]);
    }

    [TestMethod]
    public async Task ListAppsAsync_FollowsNextLinkUntilExhausted()
    {
        var handler = new QueueHandler(
            _ => JsonResponse("""{"value":[{"id":"app-1","displayName":"App 1","notes":null}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps?$skiptoken=abc"}"""),
            _ => JsonResponse("""{"value":[{"id":"app-2","displayName":"App 2","notes":null}]}"""));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var apps = await directory.ListAppsAsync(CancellationToken.None);

        Assert.HasCount(2, apps);
        Assert.AreEqual("app-1", apps[0].Id);
        Assert.AreEqual("app-2", apps[1].Id);
        Assert.HasCount(2, handler.RequestedUris);
        Assert.Contains("$skiptoken=abc", handler.RequestedUris[1]);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, 401)]
    [DataRow(HttpStatusCode.Forbidden, 403)]
    public async Task ListAppsAsync_AuthorizationFailure_ThrowsGraphAccessDeniedException(
        HttpStatusCode statusCode, int expectedStatusCode)
    {
        // Every app entry resolves through this listing, so the CLI treats a failure here as
        // identity-wide and stops the batch instead of repeating the error once per entry.
        var handler = new QueueHandler(_ => new HttpResponseMessage(statusCode));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var ex = await Assert.ThrowsExactlyAsync<GraphAccessDeniedException>(
            () => directory.ListAppsAsync(CancellationToken.None));

        Assert.AreEqual(expectedStatusCode, ex.StatusCode);
        StringAssert.StartsWith(ex.Message, "Failed to list Intune mobile apps.");
        StringAssert.Contains(ex.Message, "DeviceManagementApps.ReadWrite.All");
    }

    [TestMethod]
    public async Task ListAppsAsync_AuthorizationFailure_SurfacesGraphErrorAndCorrelationIds()
    {
        var handler = new QueueHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"error":{"code":"Forbidden","message":"Application is not authorized to perform this operation."}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.Add("client-request-id", "client-1");
            response.Headers.Add("request-id", "request-1");
            return response;
        });
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var ex = await Assert.ThrowsExactlyAsync<GraphAccessDeniedException>(
            () => directory.ListAppsAsync(CancellationToken.None));

        Assert.AreEqual("Forbidden", ex.GraphErrorCode);
        Assert.AreEqual("client-1", ex.ClientRequestId);
        Assert.AreEqual("request-1", ex.RequestId);
        StringAssert.Contains(ex.Message, "Application is not authorized to perform this operation.");
        StringAssert.Contains(ex.Message, "client-request-id=client-1");
    }

    [TestMethod]
    public async Task ListAppsAsync_NonAuthorizationErrorResponse_ThrowsGraphRequestException()
    {
        // A server-side failure is transient and app-specific, not a reason to stop the whole batch.
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => directory.ListAppsAsync(CancellationToken.None));

        Assert.AreEqual(500, ex.StatusCode);
        Assert.DoesNotContain("DeviceManagementApps.ReadWrite.All", ex.Message);
    }

    [TestMethod]
    public async Task ListAppsAsync_SuccessStatusWithMalformedBody_ThrowsGraphRequestExceptionNotJsonException()
    {
        var handler = new QueueHandler(_ => JsonResponse("<html>not json</html>"));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => directory.ListAppsAsync(CancellationToken.None));

        Assert.AreEqual(200, ex.StatusCode);
    }
}
