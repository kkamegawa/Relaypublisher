using System.Net;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphRetryHandlerTests
{
    /// <summary>Captures formatted log messages so tests can assert the HTTP method is included.</summary>
    private sealed class CapturingLogger : ILogger<GraphRetryHandler>
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

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new(responses);
        }

        public List<byte[]?> RequestBodies { get; } = [];

        public int RequestCount => RequestBodies.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No more queued responses.");
            }

            return _responses.Dequeue()(request);
        }
    }

    private static GraphClientOptions FastOptions(int maxRetryAttempts = 3) => new()
    {
        MaxRetryAttempts = maxRetryAttempts,
        BaseRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(10),
    };

    private static HttpResponseMessage ThrottledResponse(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (retryAfter is not null)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
        }

        response.Headers.TryAddWithoutValidation("client-request-id", "client-id-1");
        response.Headers.TryAddWithoutValidation("request-id", "request-id-1");
        return response;
    }

    private static (HttpClient Client, QueueHandler Inner) CreateClient(GraphClientOptions options, QueueHandler inner)
        => CreateClient(options, inner, NullLogger<GraphRetryHandler>.Instance);

    private static (HttpClient Client, QueueHandler Inner) CreateClient(GraphClientOptions options, QueueHandler inner, ILogger<GraphRetryHandler> logger)
    {
        var handler = new GraphRetryHandler(options, logger) { InnerHandler = inner };
        return (new HttpClient(handler), inner);
    }

    [TestMethod]
    public async Task SendAsync_ThrottledThenSuccess_RetriesAndReturnsSuccess()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1)),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, _) = CreateClient(FastOptions(), inner);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, inner.RequestCount);
    }

    [TestMethod]
    public async Task SendAsync_ServiceUnavailableWithoutRetryAfter_UsesExponentialBackoffAndSucceeds()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.ServiceUnavailable),
            _ => ThrottledResponse(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, _) = CreateClient(FastOptions(), inner);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(3, inner.RequestCount);
    }

    [TestMethod]
    public async Task SendAsync_ThrottledBeyondMaxRetries_ThrowsGraphRequestExceptionWithRequestIds()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero));
        var (client, _) = CreateClient(FastOptions(maxRetryAttempts: 3), inner);

        var ex = await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps"));

        Assert.AreEqual(429, ex.StatusCode);
        Assert.AreEqual("client-id-1", ex.ClientRequestId);
        Assert.AreEqual("request-id-1", ex.RequestId);
        Assert.AreEqual(4, inner.RequestCount);
        StringAssert.Contains(ex.Message, "Graph GET request to");
    }

    [TestMethod]
    public async Task SendAsync_NonThrottledFailure_ReturnsResponseWithoutThrowing()
    {
        var inner = new QueueHandler(_ => ThrottledResponse(HttpStatusCode.NotFound));
        var (client, _) = CreateClient(FastOptions(), inner);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/does-not-exist");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(1, inner.RequestCount);
    }

    [TestMethod]
    public async Task SendAsync_TransientHttpRequestException_RetriesThenSucceeds()
    {
        var inner = new QueueHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var (client, _) = CreateClient(FastOptions(), inner);

        var response = await client.GetAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, inner.RequestCount);
    }

    [TestMethod]
    public async Task SendAsync_RetriedRequestWithBody_ResendsSameContentOnEachAttempt()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1)),
            _ => new HttpResponseMessage(HttpStatusCode.Created));
        var (client, queue) = CreateClient(FastOptions(), inner);

        var payload = """{"displayName":"contoso-tool"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.HasCount(2, queue.RequestBodies);
        foreach (var body in queue.RequestBodies)
        {
            Assert.AreEqual(payload, Encoding.UTF8.GetString(body!));
        }
    }

    [TestMethod]
    public async Task SendAsync_ThrottledThenSuccess_LogsTheHttpMethod()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(1)),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger();
        var (client, _) = CreateClient(FastOptions(), inner, logger);

        await client.PatchAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1", content: null);

        Assert.IsTrue(logger.Messages.Any(m => m.Contains("PATCH", StringComparison.Ordinal)),
            "The throttled-retry warning should name the HTTP method.");
    }

    [TestMethod]
    public async Task SendAsync_TransientHttpRequestException_LogsTheHttpMethod()
    {
        var inner = new QueueHandler(
            _ => throw new HttpRequestException("connection reset"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new CapturingLogger();
        var (client, _) = CreateClient(FastOptions(), inner, logger);

        await client.PostAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps", content: null);

        Assert.IsTrue(logger.Messages.Any(m => m.Contains("POST", StringComparison.Ordinal)),
            "The transient-failure warning should name the HTTP method.");
    }

    [TestMethod]
    public async Task SendAsync_ThrottledBeyondMaxRetries_LogsTheHttpMethodInTheErrorLog()
    {
        var inner = new QueueHandler(
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero),
            _ => ThrottledResponse(HttpStatusCode.TooManyRequests, TimeSpan.Zero));
        var logger = new CapturingLogger();
        var (client, _) = CreateClient(FastOptions(maxRetryAttempts: 1), inner, logger);

        await Assert.ThrowsExactlyAsync<GraphRequestException>(
            () => client.DeleteAsync("https://graph.microsoft.com/v1.0/deviceAppManagement/mobileApps/app-1"));

        Assert.IsTrue(logger.Messages.Any(m => m.Contains("DELETE", StringComparison.Ordinal)),
            "The terminal error log should name the HTTP method.");
    }
}
