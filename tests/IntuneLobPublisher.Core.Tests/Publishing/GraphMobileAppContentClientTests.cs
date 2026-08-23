using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphMobileAppContentClientTests
{
    private const string WindowsODataType = "#microsoft.graph.win32LobApp";
    private const string MacPkgODataType = "#microsoft.graph.macOSPkgApp";
    private const string MacLobODataType = "#microsoft.graph.macOSLobApp";

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

    private static HttpResponseMessage EmptyResponse(HttpStatusCode statusCode) => new(statusCode);

    private static (GraphMobileAppContentClient Client, QueueHandler Handler) CreateClient(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new GraphMobileAppContentClient(httpClient), handler);
    }

    [TestMethod]
    public async Task CreateContentVersionAsync_PostsMobileAppContent_ReturnsId()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"@odata.type":"#microsoft.graph.mobileAppContent","id":"cv-1"}"""));

        var id = await client.CreateContentVersionAsync("app-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual("cv-1", id);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions",
            handler.Requests[0].Uri);
        StringAssert.Contains(handler.Requests[0].Body, "\"@odata.type\":\"#microsoft.graph.mobileAppContent\"");
    }

    [TestMethod]
    public async Task CreateContentVersionAsync_UseBetaTrue_RoutesToBeta()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"id":"cv-1"}"""));

        await client.CreateContentVersionAsync("app-1", MacPkgODataType, useBeta: true, CancellationToken.None);

        Assert.AreEqual(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/microsoft.graph.macOSPkgApp/contentVersions",
            handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task ListContentVersionsAsync_FollowsNextLinkAndUsesTypedRoute()
    {
        const string nextLink = "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions?$skiptoken=next";
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {"value":[{"id":"cv-1"}],"@odata.nextLink":"{{nextLink}}"}
                """),
            _ => JsonResponse(HttpStatusCode.OK, """{"value":[{"id":"cv-2"}]}"""));

        var versions = await client.ListContentVersionsAsync("app-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.HasCount(2, versions);
        Assert.AreEqual("cv-1", versions[0].Id);
        Assert.AreEqual("cv-2", versions[1].Id);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions?$select=id",
            handler.Requests[0].Uri);
        Assert.AreEqual(nextLink, handler.Requests[1].Uri);
    }

    [TestMethod]
    public async Task ListContentFilesAsync_FollowsNextLinkUsesBetaTypedRouteAndParsesIsCommitted()
    {
        const string nextLink = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/microsoft.graph.macOSPkgApp/contentVersions/cv-1/files?$skiptoken=next";
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {"value":[{"id":"file-1","uploadState":"commitFileFailed","isCommitted":false}],"@odata.nextLink":"{{nextLink}}"}
                """),
            _ => JsonResponse(HttpStatusCode.OK, """{"value":[{"id":"file-2","uploadState":"commitFileSuccess","isCommitted":true}]}"""));

        var files = await client.ListContentFilesAsync("app-1", "cv-1", MacPkgODataType, useBeta: true, CancellationToken.None);

        Assert.HasCount(2, files);
        Assert.AreEqual("file-1", files[0].Id);
        Assert.IsTrue(files[0].IsCommitted == false);
        Assert.AreEqual("commitFileFailed", files[0].UploadState);
        Assert.AreEqual("file-2", files[1].Id);
        Assert.IsTrue(files[1].IsCommitted == true);
        Assert.AreEqual(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/microsoft.graph.macOSPkgApp/contentVersions/cv-1/files?$select=id,isCommitted,uploadState",
            handler.Requests[0].Uri);
        Assert.AreEqual(nextLink, handler.Requests[1].Uri);
    }

    [TestMethod]
    public async Task DeleteContentFileAsync_NoContent_UsesUntypedBetaRouteAndNoBody()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        await client.DeleteContentFileAsync("app-1", "cv-1", "file-1", MacPkgODataType, useBeta: true, CancellationToken.None);

        Assert.AreEqual("DELETE", handler.Requests[0].Method);
        Assert.AreEqual(
            "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/contentVersions/cv-1/files/file-1",
            handler.Requests[0].Uri);
        Assert.IsNull(handler.Requests[0].Body);
    }

    [TestMethod]
    public async Task DeleteContentFileAsync_NotFound_IsIdempotent()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":"itemNotFound","message":"already deleted"}}"""));

        await client.DeleteContentFileAsync("app-1", "cv-1", "file-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual("DELETE", handler.Requests[0].Method);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/contentVersions/cv-1/files/file-1",
            handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task DeleteContentFileAsync_UnrecognizedODataType_ThrowsWithoutCallingGraph()
    {
        var (client, handler) = CreateClient(
            _ => throw new InvalidOperationException("Should not reach the network for an unrecognized OData type."));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.DeleteContentFileAsync(
                "app-1", "cv-1", "file-1", "#microsoft.graph.unknownAppType", useBeta: false, CancellationToken.None));

        StringAssert.Contains(ex.Message, "unknownAppType");
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task CreateContentFileAsync_PostsNameSizeAndSizeEncrypted_ReturnsId()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"id":"file-1","uploadState":"azureStorageUriRequestPending"}"""));

        var id = await client.CreateContentFileAsync(
            "app-1", "cv-1", "IntunePackage.intunewin", 100, 128, WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual("file-1", id);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions/cv-1/files",
            handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"name\":\"IntunePackage.intunewin\"");
        StringAssert.Contains(body, "\"size\":100");
        StringAssert.Contains(body, "\"sizeEncrypted\":128");
    }

    [TestMethod]
    public async Task GetContentFileAsync_ParsesUploadStateAndAzureStorageUri()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """
            {"id":"file-1","uploadState":"azureStorageUriRequestSuccess","azureStorageUri":"https://sas.example/blob",
             "azureStorageUriExpirationDateTime":"2026-07-05T12:00:00Z"}
            """));

        var file = await client.GetContentFileAsync("app-1", "cv-1", "file-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual("azureStorageUriRequestSuccess", file.UploadState);
        Assert.AreEqual("https://sas.example/blob", file.AzureStorageUri);
        Assert.AreEqual(DateTimeOffset.Parse("2026-07-05T12:00:00Z"), file.AzureStorageUriExpirationDateTime);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions/cv-1/files/file-1",
            handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task RenewUploadAsync_PostsWithNoBody()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        await client.RenewUploadAsync("app-1", "cv-1", "file-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions/cv-1/files/file-1/renewUpload",
            handler.Requests[0].Uri);
        Assert.IsNull(handler.Requests[0].Body);
    }

    [TestMethod]
    public async Task CommitFileAsync_PostsFileEncryptionInfoAsBase64()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));
        var encryptionInfo = new FileEncryptionInfoPayload
        {
            EncryptionKey = [1, 2, 3],
            InitializationVector = new byte[16],
            Mac = new byte[32],
            MacKey = new byte[32],
            ProfileIdentifier = "ProfileVersion1",
            FileDigest = [4, 5, 6],
            FileDigestAlgorithm = "SHA256",
        };

        await client.CommitFileAsync("app-1", "cv-1", "file-1", encryptionInfo, WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.win32LobApp/contentVersions/cv-1/files/file-1/commit",
            handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"fileEncryptionInfo\"");
        StringAssert.Contains(body, $"\"encryptionKey\":\"{Convert.ToBase64String([1, 2, 3])}\"");
        StringAssert.Contains(body, "\"profileIdentifier\":\"ProfileVersion1\"");
    }

    [TestMethod]
    public async Task PatchCommittedContentVersionAsync_PatchesWithoutNotes()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.OK));

        await client.PatchCommittedContentVersionAsync("app-1", "cv-1", WindowsODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual("PATCH", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"committedContentVersion\":\"cv-1\"");
        StringAssert.Contains(body, $"\"@odata.type\":\"{WindowsODataType}\"");
        Assert.IsFalse(body.Contains("\"notes\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PatchCommittedContentVersionAsync_UseBetaTrue_RoutesToBeta()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.OK));

        await client.PatchCommittedContentVersionAsync("app-1", "cv-1", "#microsoft.graph.macOSPkgApp", useBeta: true, CancellationToken.None);

        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1", handler.Requests[0].Uri);
        StringAssert.Contains(handler.Requests[0].Body, "\"@odata.type\":\"#microsoft.graph.macOSPkgApp\"");
    }

    [TestMethod]
    public async Task PatchNotesAsync_PatchesWithoutCommittedContentVersion()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.OK));

        await client.PatchNotesAsync("app-1", """{"managedBy":"intune-lob-manifest"}""", WindowsODataType, useBeta: false, CancellationToken.None);

        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"notes\":");
        Assert.IsFalse(body.Contains("committedContentVersion", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetPublishingStateAsync_SelectsPublishingStateAndReturnsValue()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """{"publishingState":"published"}"""));

        var state = await client.GetPublishingStateAsync("app-1", useBeta: false, CancellationToken.None);

        Assert.AreEqual("published", state);
        StringAssert.Contains(handler.Requests[0].Uri, "$select=publishingState");
    }

    [TestMethod]
    public async Task ErrorResponse_ThrowsGraphRequestExceptionWithRequestIds()
    {
        var (client, _) = CreateClient(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("client-request-id", "client-id-1");
            response.Headers.TryAddWithoutValidation("request-id", "request-id-1");
            return response;
        });

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateContentVersionAsync("app-1", WindowsODataType, useBeta: false, CancellationToken.None));

        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("client-id-1", ex.ClientRequestId);
        Assert.AreEqual("request-id-1", ex.RequestId);
    }

    [TestMethod]
    public async Task CreateContentVersionAsync_MacOsLobODataType_UsesMacOsLobAppCastSegment()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.Created, """{"id":"cv-1"}"""));

        await client.CreateContentVersionAsync("app-1", MacLobODataType, useBeta: false, CancellationToken.None);

        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/microsoft.graph.macOSLobApp/contentVersions",
            handler.Requests[0].Uri);
    }

    [TestMethod]
    public async Task CreateContentVersionAsync_UnrecognizedODataType_ThrowsGraphRequestExceptionWithoutCallingGraph()
    {
        var (client, handler) = CreateClient(
            _ => throw new InvalidOperationException("Should not reach the network for an unrecognized OData type."));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateContentVersionAsync("app-1", "#microsoft.graph.unknownAppType", useBeta: false, CancellationToken.None));

        StringAssert.Contains(ex.Message, "unknownAppType");
        Assert.IsEmpty(handler.Requests);
    }
}
