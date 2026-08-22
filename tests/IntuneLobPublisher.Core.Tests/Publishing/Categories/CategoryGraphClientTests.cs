using System.Net;
using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Categories;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

[TestClass]
public sealed class CategoryGraphClientTests
{
    private const string AppId = "app-1";
    private const string CategoryId = "cat-1";

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);

        public List<(string Method, string Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No more queued responses.");
            }

            return _responses.Dequeue()(request);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage EmptyResponse(HttpStatusCode statusCode) => new(statusCode);

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string code, string message)
        => JsonResponse(statusCode, $$$"""{"error":{"code":"{{{code}}}","message":"{{{message}}}"}}""");

    private static (CategoryGraphClient Client, QueueHandler Handler) CreateClient(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        => CreateClient(new Uri("https://graph.microsoft.com/v1.0/"), responses);

    private static (CategoryGraphClient Client, QueueHandler Handler) CreateClient(
        Uri baseAddress, params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        return (new CategoryGraphClient(httpClient), handler);
    }

    [TestMethod]
    public async Task ListTenantCategoriesAsync_FollowsNextLink()
    {
        var nextLink = "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileAppCategories?$skiptoken=page2";
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "value": [ { "id": "cat-1", "displayName": "Business Apps" } ],
                  "@odata.nextLink": "{{nextLink}}"
                }
                """),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "value": [ { "id": "cat-2", "displayName": "Productivity" } ] }
                """));

        var categories = await client.ListTenantCategoriesAsync(useBeta: false, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "Business Apps", "Productivity" }, categories.Select(c => c.DisplayName).ToList());
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileAppCategories?$select=id,displayName",
            handler.Requests[0].Uri);
        Assert.AreEqual(nextLink, handler.Requests[1].Uri);
    }

    [TestMethod]
    public async Task ListTenantCategoriesAsync_UsesBetaForPkgApps()
    {
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, """{ "value": [] }"""));

        await client.ListTenantCategoriesAsync(useBeta: true, CancellationToken.None);

        StringAssert.StartsWith(
            handler.Requests.Single().Uri, "https://graph.microsoft.com/beta/deviceAppManagement/mobileAppCategories");
    }

    [TestMethod]
    public async Task ListTenantCategoriesAsync_Forbidden_ThrowsGraphAccessDenied()
    {
        // Identity-wide: no entry that declares Categories could succeed, so the CLI aborts the batch (#94).
        var (client, _) = CreateClient(
            _ => ErrorResponse(HttpStatusCode.Forbidden, "Forbidden", "Access denied"));

        var exception = await Assert.ThrowsExactlyAsync<GraphAccessDeniedException>(
            () => client.ListTenantCategoriesAsync(useBeta: false, CancellationToken.None));

        Assert.AreEqual(403, exception.StatusCode);
    }

    [TestMethod]
    public async Task ListTenantCategoriesAsync_ServerError_ThrowsGraphRequestException()
    {
        var (client, _) = CreateClient(
            _ => ErrorResponse(HttpStatusCode.InternalServerError, "InternalError", "Boom"));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.ListTenantCategoriesAsync(useBeta: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ListAppCategoriesAsync_FollowsNextLinkAndUsesTheAppScopedPath()
    {
        var nextLink = "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/categories?$skiptoken=page2";
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "value": [ { "id": "cat-1", "displayName": "Business Apps" } ],
                  "@odata.nextLink": "{{nextLink}}"
                }
                """),
            _ => JsonResponse(HttpStatusCode.OK, """
                { "value": [ { "id": "cat-9", "displayName": "Legacy" } ] }
                """));

        var categories = await client.ListAppCategoriesAsync(AppId, useBeta: false, CancellationToken.None);

        Assert.AreEqual(2, categories.Count);
        Assert.AreEqual(
            "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/categories?$select=id,displayName",
            handler.Requests[0].Uri);
        Assert.AreEqual(nextLink, handler.Requests[1].Uri);
    }

    [TestMethod]
    public async Task ListAppCategoriesAsync_Forbidden_StaysPerAppGraphRequestException()
    {
        // A 403 confined to one app must not abort the whole batch.
        var (client, _) = CreateClient(
            _ => ErrorResponse(HttpStatusCode.Forbidden, "Forbidden", "Access denied"));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.ListAppCategoriesAsync(AppId, useBeta: false, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false, "v1.0")]
    [DataRow(true, "beta")]
    public async Task AddCategoryAsync_PostsRefWithMatchingODataIdVersion(bool useBeta, string version)
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        var added = await client.AddCategoryAsync(AppId, CategoryId, useBeta, CancellationToken.None);

        Assert.IsTrue(added);
        var request = handler.Requests.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual(
            $"https://graph.microsoft.com/{version}/deviceAppManagement/mobileApps/app-1/categories/$ref",
            request.Uri);

        using var document = JsonDocument.Parse(request.Body!);
        Assert.AreEqual(
            $"https://graph.microsoft.com/{version}/deviceAppManagement/mobileAppCategories/cat-1",
            document.RootElement.GetProperty("@odata.id").GetString());
    }

    [TestMethod]
    public async Task AddCategoryAsync_BuildsODataIdFromTheConfiguredAuthority()
    {
        // The host must come from GraphClientOptions.BaseAddress, not a hardcoded graph.microsoft.com,
        // so stub servers and sovereign clouds work.
        var (client, handler) = CreateClient(
            new Uri("https://graph.example.test:8443/v1.0/"),
            _ => EmptyResponse(HttpStatusCode.NoContent));

        await client.AddCategoryAsync(AppId, CategoryId, useBeta: true, CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Requests.Single().Body!);
        Assert.AreEqual(
            "https://graph.example.test:8443/beta/deviceAppManagement/mobileAppCategories/cat-1",
            document.RootElement.GetProperty("@odata.id").GetString());
    }

    [TestMethod]
    public async Task AddCategoryAsync_EscapesIdsInThePathAndReference()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        await client.AddCategoryAsync("app/1", "cat 1", useBeta: false, CancellationToken.None);

        var request = handler.Requests.Single();
        StringAssert.Contains(request.Uri, "mobileApps/app%2F1/categories/$ref");
        using var document = JsonDocument.Parse(request.Body!);
        StringAssert.EndsWith(document.RootElement.GetProperty("@odata.id").GetString()!, "mobileAppCategories/cat%201");
    }

    [TestMethod]
    [DataRow(400, "BadRequest", "One or more added object references already exist for the following modified properties: 'categories'.")]
    [DataRow(409, "Conflict", "The relationship is already linked.")]
    public async Task AddCategoryAsync_AlreadyRelated_IsTreatedAsSuccess(int statusCode, string code, string message)
    {
        // GraphRetryHandler replays POST bodies, so a duplicate add must converge instead of failing.
        var (client, _) = CreateClient(_ => ErrorResponse((HttpStatusCode)statusCode, code, message));

        var added = await client.AddCategoryAsync(AppId, CategoryId, useBeta: false, CancellationToken.None);

        Assert.IsFalse(added, "An existing relationship is success, but it changed nothing.");
    }

    [TestMethod]
    [DataRow(400, "BadRequest", "The category id is malformed.")]
    [DataRow(409, "Conflict", "The app is being modified by another operation.")]
    [DataRow(404, "NotFound", "Resource not found.")]
    public async Task AddCategoryAsync_UndecidableFailure_Throws(int statusCode, string code, string message)
    {
        var (client, _) = CreateClient(_ => ErrorResponse((HttpStatusCode)statusCode, code, message));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.AddCategoryAsync(AppId, CategoryId, useBeta: false, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false, "v1.0")]
    [DataRow(true, "beta")]
    public async Task RemoveCategoryAsync_DeletesTheAppSideRef(bool useBeta, string version)
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        var removed = await client.RemoveCategoryAsync(AppId, CategoryId, useBeta, CancellationToken.None);

        Assert.IsTrue(removed);
        var request = handler.Requests.Single();
        Assert.AreEqual("DELETE", request.Method);
        Assert.AreEqual(
            $"https://graph.microsoft.com/{version}/deviceAppManagement/mobileApps/app-1/categories/cat-1/$ref",
            request.Uri);
    }

    [TestMethod]
    public async Task RemoveCategoryAsync_AlreadyGone_IsTreatedAsSuccess()
    {
        var (client, _) = CreateClient(_ => ErrorResponse(HttpStatusCode.NotFound, "NotFound", "Resource not found."));

        var removed = await client.RemoveCategoryAsync(AppId, CategoryId, useBeta: false, CancellationToken.None);

        Assert.IsFalse(removed);
    }

    [TestMethod]
    public async Task RemoveCategoryAsync_OtherFailure_Throws()
    {
        var (client, _) = CreateClient(_ => ErrorResponse(HttpStatusCode.BadRequest, "BadRequest", "Malformed id."));

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.RemoveCategoryAsync(AppId, CategoryId, useBeta: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task AddCategoryAsync_ThrottledThenSuccess_RetriesThroughTheSharedRetryHandler()
    {
        // The category client rides the same retry pipeline: 429 with Retry-After is replayed with the body.
        var inner = new QueueHandler(
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            },
            _ => EmptyResponse(HttpStatusCode.NoContent));
        var options = new GraphClientOptions
        {
            MaxRetryAttempts = 3,
            BaseRetryDelay = TimeSpan.FromMilliseconds(1),
            MaxRetryDelay = TimeSpan.FromMilliseconds(10),
        };
        var retryHandler = new GraphRetryHandler(options, NullLogger<GraphRetryHandler>.Instance) { InnerHandler = inner };
        var httpClient = new HttpClient(retryHandler) { BaseAddress = options.BaseAddress };
        var client = new CategoryGraphClient(httpClient);

        var added = await client.AddCategoryAsync(AppId, CategoryId, useBeta: false, CancellationToken.None);

        Assert.IsTrue(added);
        Assert.AreEqual(2, inner.Requests.Count);
        Assert.AreEqual(inner.Requests[0].Body, inner.Requests[1].Body, "The $ref body must be replayed verbatim.");
    }
}
