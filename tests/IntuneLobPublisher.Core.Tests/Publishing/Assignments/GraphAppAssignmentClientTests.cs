using System.Net;
using System.Text;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class GraphAppAssignmentClientTests
{
    private const string AppId = "app-1";

    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid FilterA = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    /// <summary>Records requests (method, URI, body) and replays queued responses.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingHandler(params HttpResponseMessage[] responses) => _responses = new(responses);

        public List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, body));
            return _responses.Dequeue();
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GraphAppAssignmentClient CreateClient(RecordingHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/v1.0/") });

    private static DesiredAssignment GroupDesired(
        string intent = "required",
        AssignmentFilter? filter = null,
        NormalizedAssignmentSettings? settings = null,
        bool isExclusion = false)
        => new(
            new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, isExclusion),
            intent,
            filter,
            settings ?? NormalizedAssignmentSettings.Default);

    [TestMethod]
    public async Task GetAssignmentsAsync_UsesBetaEndpointAndParsesTargets()
    {
        var handler = new RecordingHandler(Json($$"""
            {
              "value": [
                {
                  "id": "a-1",
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
                },
                {
                  "id": "a-2",
                  "intent": "required",
                  "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" }
                },
                {
                  "id": "a-3",
                  "intent": "required",
                  "target": {
                    "@odata.type": "#microsoft.graph.exclusionGroupAssignmentTarget",
                    "groupId": "{{GroupA}}",
                    "deviceAndAppManagementAssignmentFilterId": "00000000-0000-0000-0000-000000000000",
                    "deviceAndAppManagementAssignmentFilterType": "none"
                  }
                }
              ]
            }
            """));
        var client = CreateClient(handler);

        var assignments = await client.GetAssignmentsAsync(AppId, CancellationToken.None);

        Assert.AreEqual(new Uri("https://unit.test/beta/deviceAppManagement/mobileApps/app-1/assignments"), handler.Requests[0].Uri);

        Assert.HasCount(3, assignments);
        Assert.AreEqual(new CurrentAssignment(
            "a-1",
            new AssignmentTargetKey(AssignmentTargetKind.Group, GroupA, IsExclusion: false),
            "available",
            new AssignmentFilter(FilterA, AssignmentFilterMode.Include),
            new NormalizedAssignmentSettings("hideAll", 1440)), assignments[0]);
        Assert.AreEqual(new AssignmentTargetKey(AssignmentTargetKind.AllDevices, null, false), assignments[1].Key);
        Assert.AreEqual(NormalizedAssignmentSettings.Default, assignments[1].Settings);

        // Exclusions come back pinned to "required" with the "none" filter normalized away.
        Assert.IsTrue(assignments[2].Key.IsExclusion);
        Assert.AreEqual("required", assignments[2].Intent);
        Assert.IsNull(assignments[2].Filter);
    }

    [TestMethod]
    public async Task GetAssignmentsAsync_FollowsNextLink()
    {
        var handler = new RecordingHandler(
            Json($$"""
                {
                  "value": [{ "id": "a-1", "intent": "required", "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" } }],
                  "@odata.nextLink": "https://unit.test/beta/deviceAppManagement/mobileApps/app-1/assignments?$skip=1"
                }
                """),
            Json("""
                { "value": [{ "id": "a-2", "intent": "available", "target": { "@odata.type": "#microsoft.graph.allLicensedUsersAssignmentTarget" } }] }
                """));
        var client = CreateClient(handler);

        var assignments = await client.GetAssignmentsAsync(AppId, CancellationToken.None);

        Assert.HasCount(2, assignments);
        Assert.HasCount(2, handler.Requests);
        StringAssert.Contains(handler.Requests[1].Uri.Query, "skip=1");
    }

    [TestMethod]
    public async Task GetAssignmentsAsync_Failure_ThrowsGraphRequestException()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("request-id", "req-1");
        var client = CreateClient(new RecordingHandler(response));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.GetAssignmentsAsync(AppId, CancellationToken.None));

        Assert.AreEqual(403, ex.StatusCode);
        Assert.AreEqual("req-1", ex.RequestId);
    }

    [TestMethod]
    public async Task GetAssignmentsAsync_UnknownTargetType_Throws()
    {
        var client = CreateClient(new RecordingHandler(Json("""
            { "value": [{ "id": "a-1", "intent": "required", "target": { "@odata.type": "#microsoft.graph.configurationManagerCollectionAssignmentTarget" } }] }
            """)));

        await Assert.ThrowsExactlyAsync<AssignmentPlanningException>(
            () => client.GetAssignmentsAsync(AppId, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_SendsWin32PayloadWithFilterAndSettings()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created));
        var client = CreateClient(handler);
        var desired = GroupDesired(
            filter: new AssignmentFilter(FilterA, AssignmentFilterMode.Exclude),
            settings: new NormalizedAssignmentSettings("showReboot", 60));

        await client.CreateAssignmentAsync(AppId, desired, isWin32: true, CancellationToken.None);

        var (method, uri, body) = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Post, method);
        Assert.AreEqual(new Uri("https://unit.test/beta/deviceAppManagement/mobileApps/app-1/assignments"), uri);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.AreEqual("#microsoft.graph.mobileAppAssignment", root.GetProperty("@odata.type").GetString());
        Assert.AreEqual("required", root.GetProperty("intent").GetString());
        Assert.IsFalse(root.TryGetProperty("id", out _), "id must not be sent on create.");

        var target = root.GetProperty("target");
        Assert.AreEqual("#microsoft.graph.groupAssignmentTarget", target.GetProperty("@odata.type").GetString());
        Assert.AreEqual(GroupA.ToString("D"), target.GetProperty("groupId").GetString());
        Assert.AreEqual(FilterA.ToString("D"), target.GetProperty("deviceAndAppManagementAssignmentFilterId").GetString());
        Assert.AreEqual("exclude", target.GetProperty("deviceAndAppManagementAssignmentFilterType").GetString());

        var settings = root.GetProperty("settings");
        Assert.AreEqual("#microsoft.graph.win32LobAppAssignmentSettings", settings.GetProperty("@odata.type").GetString());
        Assert.AreEqual("showReboot", settings.GetProperty("notifications").GetString());
        var restart = settings.GetProperty("restartSettings");
        Assert.AreEqual(60, restart.GetProperty("gracePeriodInMinutes").GetInt32());
        Assert.AreEqual(15, restart.GetProperty("countdownDisplayBeforeRestartInMinutes").GetInt32());
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_NonWin32_OmitsSettings()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created));
        var client = CreateClient(handler);

        await client.CreateAssignmentAsync(AppId, GroupDesired(), isWin32: false, CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.IsFalse(json.RootElement.TryGetProperty("settings", out _));
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_Exclusion_UsesExclusionTargetAndOmitsSettings()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Created));
        var client = CreateClient(handler);

        await client.CreateAssignmentAsync(AppId, GroupDesired(isExclusion: true), isWin32: true, CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        var root = json.RootElement;
        Assert.AreEqual("#microsoft.graph.exclusionGroupAssignmentTarget", root.GetProperty("target").GetProperty("@odata.type").GetString());
        Assert.IsFalse(root.TryGetProperty("settings", out _), "Graph rejects settings on exclusion assignments.");
    }

    [TestMethod]
    public async Task UpdateAssignmentAsync_PatchesTheAssignment()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);

        await client.UpdateAssignmentAsync(AppId, "a-1", GroupDesired(intent: "available"), isWin32: true, CancellationToken.None);

        var (method, uri, body) = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Patch, method);
        Assert.AreEqual(new Uri("https://unit.test/beta/deviceAppManagement/mobileApps/app-1/assignments/a-1"), uri);

        using var json = JsonDocument.Parse(body!);
        Assert.AreEqual("available", json.RootElement.GetProperty("intent").GetString());
    }

    [TestMethod]
    public async Task DeleteAssignmentAsync_DeletesTheAssignment()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.DeleteAssignmentAsync(AppId, "a-1", CancellationToken.None);

        var (method, uri, _) = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Delete, method);
        Assert.AreEqual(new Uri("https://unit.test/beta/deviceAppManagement/mobileApps/app-1/assignments/a-1"), uri);
    }

    [TestMethod]
    public async Task CreateAssignmentAsync_Failure_ThrowsGraphRequestException()
    {
        var client = CreateClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)));

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.CreateAssignmentAsync(AppId, GroupDesired(), isWin32: true, CancellationToken.None));

        Assert.AreEqual(400, ex.StatusCode);
    }
}
