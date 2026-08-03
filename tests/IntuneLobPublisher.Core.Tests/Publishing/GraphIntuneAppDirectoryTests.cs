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
    public async Task ListAppsAsync_ErrorResponse_ThrowsGraphRequestException()
    {
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var directory = new GraphIntuneAppDirectory(CreateClient(handler));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => directory.ListAppsAsync(CancellationToken.None));

        Assert.AreEqual(403, ex.StatusCode);
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
