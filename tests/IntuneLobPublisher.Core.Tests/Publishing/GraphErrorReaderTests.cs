using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphErrorReaderTests
{
    private const string RequestUri = "/beta/deviceAppManagement/mobileApps";

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string? body = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return response;
    }

    [TestMethod]
    public async Task ReadFailureAsync_GraphErrorBody_SurfacesCodeAndMessage()
    {
        using var response = Response(
            HttpStatusCode.Forbidden,
            """{"error":{"code":"Forbidden","message":"Application is not authorized to perform this operation."}}""");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.AreEqual(403, failure.StatusCode);
        Assert.AreEqual("Forbidden", failure.ErrorCode);
        StringAssert.Contains(failure.Summary, "(Forbidden)");
        StringAssert.Contains(failure.Summary, "Application is not authorized to perform this operation.");
    }

    [TestMethod]
    public async Task ReadFailureAsync_CorrelationHeaders_AreIncludedInTheSummary()
    {
        using var response = Response(HttpStatusCode.Forbidden);
        response.Headers.Add("client-request-id", "client-1");
        response.Headers.Add("request-id", "request-1");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.AreEqual("client-1", failure.ClientRequestId);
        Assert.AreEqual("request-1", failure.RequestId);
        StringAssert.Contains(failure.Summary, "client-request-id=client-1");
        StringAssert.Contains(failure.Summary, "request-id=request-1");
    }

    [TestMethod]
    public async Task ReadFailureAsync_MissingHeaders_OmitsTheCorrelationSection()
    {
        using var response = Response(HttpStatusCode.InternalServerError);

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.IsNull(failure.ClientRequestId);
        Assert.IsNull(failure.RequestId);
        Assert.DoesNotContain("client-request-id", failure.Summary);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    public async Task ReadFailureAsync_AuthorizationFailures_ExplainTheApplicationPermissionRequirement(
        HttpStatusCode statusCode)
    {
        using var response = Response(statusCode);

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        // The failure mode this hint exists for: the permission is registered as a delegated one, which
        // an app-only token never carries, so Graph refuses even though the portal shows it as granted.
        StringAssert.Contains(failure.Summary, "DeviceManagementApps.ReadWrite.All");
        StringAssert.Contains(failure.Summary, "not a delegated one");
        StringAssert.Contains(failure.Summary, "'roles' claim");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.BadRequest)]
    public async Task ReadFailureAsync_OtherFailures_DoNotSuggestAPermissionProblem(HttpStatusCode statusCode)
    {
        using var response = Response(statusCode);

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.DoesNotContain("DeviceManagementApps.ReadWrite.All", failure.Summary);
    }

    [TestMethod]
    public async Task ReadFailureAsync_EmptyBody_FallsBackToTheStatusCode()
    {
        using var response = Response(HttpStatusCode.Forbidden);

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.IsNull(failure.ErrorCode);
        StringAssert.Contains(failure.Summary, "returned 403.");
    }

    [TestMethod]
    public async Task ReadFailureAsync_NonJsonBody_DoesNotThrow()
    {
        using var response = Response(HttpStatusCode.BadGateway, "<html>gateway error</html>");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.AreEqual(502, failure.StatusCode);
        Assert.IsNull(failure.ErrorCode);
    }

    [TestMethod]
    public async Task ReadFailureAsync_JsonWithoutErrorObject_DoesNotThrow()
    {
        using var response = Response(HttpStatusCode.BadRequest, """{"value":[]}""");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.IsNull(failure.ErrorCode);
    }

    [TestMethod]
    public async Task ReadFailureAsync_MultiLineGraphMessage_IsCollapsedToOneLine()
    {
        using var response = Response(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"BadRequest","message":"First line.\n  Second line."}}""");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        Assert.DoesNotContain("\n", failure.Summary);
        StringAssert.Contains(failure.Summary, "First line. Second line.");
    }

    [TestMethod]
    public async Task ReadFailureAsync_VeryLongGraphMessage_IsTruncated()
    {
        var longMessage = new string('x', 5000);
        using var response = Response(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"BadRequest\",\"message\":\"" + longMessage + "\"}}");

        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        StringAssert.Contains(failure.Summary, "...");
        Assert.IsLessThan(1000, failure.Summary.Length);
    }

    [TestMethod]
    public async Task ToRequestException_CarriesTheFailureDetails()
    {
        using var response = Response(
            HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Denied."}}""");
        response.Headers.Add("request-id", "request-1");
        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        var exception = failure.ToRequestException("Failed to list Intune mobile apps.");

        Assert.AreEqual(403, exception.StatusCode);
        Assert.AreEqual("Forbidden", exception.GraphErrorCode);
        Assert.AreEqual("request-1", exception.RequestId);
        StringAssert.StartsWith(exception.Message, "Failed to list Intune mobile apps. ");
    }

    [TestMethod]
    public async Task ToAccessDeniedException_CarriesTheFailureDetails()
    {
        using var response = Response(
            HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Denied."}}""");
        var failure = await GraphErrorReader.ReadFailureAsync(response, RequestUri, CancellationToken.None);

        var exception = failure.ToAccessDeniedException();

        Assert.IsInstanceOfType<GraphAccessDeniedException>(exception);
        Assert.AreEqual(403, exception.StatusCode);
        Assert.AreEqual("Forbidden", exception.GraphErrorCode);
    }
}
