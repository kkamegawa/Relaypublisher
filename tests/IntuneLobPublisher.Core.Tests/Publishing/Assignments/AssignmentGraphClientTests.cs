using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class AssignmentGraphClientTests
{
    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

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

    private static (AssignmentGraphClient Client, QueueHandler Handler) CreateClient(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        return (new AssignmentGraphClient(httpClient), handler);
    }

    private static DesiredAssignment Desired(
        AssignmentFilter? filter = null,
        NormalizedAssignmentSettings? settings = null)
        => new(
            new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false),
            "required",
            filter,
            settings ?? NormalizedAssignmentSettings.Default);

    [TestMethod]
    public async Task ListAssignmentsAsync_UsesBetaAndFollowsNextLink()
    {
        var nextLink = "https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/assignments?$skiptoken=page2";
        var (client, handler) = CreateClient(
            _ => JsonResponse(HttpStatusCode.OK, $$"""
                {
                  "value": [
                    {
                      "id": "assignment-1",
                      "intent": "available",
                      "target": {
                        "@odata.type": "#microsoft.graph.groupAssignmentTarget",
                        "groupId": "{{GroupA}}",
                        "deviceAndAppManagementAssignmentFilterId": "{{FilterA}}",
                        "deviceAndAppManagementAssignmentFilterType": "include"
                      },
                      "settings": {
                        "@odata.type": "#microsoft.graph.win32LobAppAssignmentSettings",
                        "notifications": "hideAll",
                        "restartSettings": { "gracePeriodInMinutes": 1440 }
                      }
                    }
                  ],
                  "@odata.nextLink": "{{nextLink}}"
                }
                """),
            _ => JsonResponse(HttpStatusCode.OK, """
                {
                  "value": [
                    {
                      "id": "assignment-2",
                      "intent": "required",
                      "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" }
                    }
                  ]
                }
                """));

        var assignments = await client.ListAssignmentsAsync("app-1", CancellationToken.None);

        Assert.HasCount(2, assignments);
        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/assignments", handler.Requests[0].Uri);
        Assert.AreEqual(nextLink, handler.Requests[1].Uri);
        Assert.AreEqual(new AssignmentFilter(FilterA, AssignmentFilterMode.Include), assignments[0].Filter);
        Assert.AreEqual(new NormalizedAssignmentSettings("hideAll", 1440), assignments[0].Settings);
        Assert.AreEqual(AssignmentTargetKind.AllDevices, assignments[1].Key.Kind);
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_PostsV1ForAssignmentWithoutFilter()
    {
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"assignment-1"}"""));

        var id = await client.CreateAssignmentAsync("app-1", Desired(), CancellationToken.None);

        Assert.AreEqual("assignment-1", id);
        Assert.AreEqual("POST", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/assignments", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.mobileAppAssignment\"");
        StringAssert.Contains(body, "\"intent\":\"required\"");
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.groupAssignmentTarget\"");
        StringAssert.Contains(body, $"\"groupId\":\"{GroupA}\"");
        Assert.IsFalse(body.Contains("settings", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_PostsBetaPayloadForAssignmentWithFilterAndSettings()
    {
        var desired = Desired(
            new AssignmentFilter(FilterA, AssignmentFilterMode.Exclude),
            new NormalizedAssignmentSettings("showReboot", 30));
        var (client, handler) = CreateClient(_ => JsonResponse(HttpStatusCode.Created, """{"id":"assignment-1"}"""));

        await client.CreateAssignmentAsync("app-1", desired, CancellationToken.None);

        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/assignments", handler.Requests[0].Uri);
        var body = handler.Requests[0].Body!;
        StringAssert.Contains(body, $"\"deviceAndAppManagementAssignmentFilterId\":\"{FilterA}\"");
        StringAssert.Contains(body, "\"deviceAndAppManagementAssignmentFilterType\":\"exclude\"");
        StringAssert.Contains(body, "\"@odata.type\":\"#microsoft.graph.win32LobAppAssignmentSettings\"");
        StringAssert.Contains(body, "\"notifications\":\"showReboot\"");
        StringAssert.Contains(body, "\"gracePeriodInMinutes\":30");
    }

    [TestMethod]
    public async Task UpdateAssignmentAsync_UsesBetaWhenRemovingExistingFilter()
    {
        var current = new CurrentAssignment(
            "assignment-1",
            new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false),
            "required",
            new AssignmentFilter(FilterA, AssignmentFilterMode.Include),
            NormalizedAssignmentSettings.Default);
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.OK));

        await client.UpdateAssignmentAsync("app-1", current, Desired(), CancellationToken.None);

        Assert.AreEqual("PATCH", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/beta/deviceAppManagement/mobileApps/app-1/assignments/assignment-1", handler.Requests[0].Uri);
        Assert.IsFalse(handler.Requests[0].Body!.Contains("deviceAndAppManagementAssignmentFilterId", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeleteAssignmentAsync_DeletesV1Path()
    {
        var (client, handler) = CreateClient(_ => EmptyResponse(HttpStatusCode.NoContent));

        await client.DeleteAssignmentAsync("app-1", "assignment-1", CancellationToken.None);

        Assert.AreEqual("DELETE", handler.Requests[0].Method);
        Assert.AreEqual("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1/assignments/assignment-1", handler.Requests[0].Uri);
        Assert.IsNull(handler.Requests[0].Body);
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
            () => client.CreateAssignmentAsync("app-1", Desired(), CancellationToken.None));

        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("client-id-1", ex.ClientRequestId);
        Assert.AreEqual("request-id-1", ex.RequestId);
    }
}
