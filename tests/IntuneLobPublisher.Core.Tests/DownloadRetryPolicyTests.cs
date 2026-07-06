using System.Net;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class DownloadRetryPolicyTests
{
    private static DownloadRetryPolicy CreatePolicy(int maxRetryAttempts = 3)
        => new(
            new SourceRetryOptions { MaxRetryAttempts = maxRetryAttempts, BaseRetryDelay = TimeSpan.Zero },
            NullLogger<DownloadRetryPolicy>.Instance);

    [TestMethod]
    public async Task ExecuteAsync_TransientFailureThenSuccess_ReturnsResult()
    {
        var policy = CreatePolicy();
        var attempts = 0;

        var result = await policy.ExecuteAsync<string>("op", _ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);
            }

            return Task.FromResult("ok");
        }, CancellationToken.None);

        Assert.AreEqual("ok", result);
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_RetriesExhausted_RethrowsOriginalException()
    {
        var policy = CreatePolicy(maxRetryAttempts: 2);
        var attempts = 0;
        var exception = new HttpRequestException("still down", null, HttpStatusCode.ServiceUnavailable);

        var thrown = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => policy.ExecuteAsync<string>("op", _ =>
            {
                attempts++;
                throw exception;
            }, CancellationToken.None));

        Assert.AreSame(exception, thrown);
        Assert.AreEqual(3, attempts); // initial attempt + 2 retries
    }

    [TestMethod]
    public async Task ExecuteAsync_NonTransientFailure_DoesNotRetry()
    {
        var policy = CreatePolicy();
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => policy.ExecuteAsync<string>("op", _ =>
            {
                attempts++;
                throw new HttpRequestException("not found", null, HttpStatusCode.NotFound);
            }, CancellationToken.None));

        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnrelatedException_DoesNotRetry()
    {
        var policy = CreatePolicy();
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => policy.ExecuteAsync<string>("op", _ =>
            {
                attempts++;
                throw new InvalidOperationException("bug");
            }, CancellationToken.None));

        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    [DataRow(null, true)] // network-level error without a response
    [DataRow(HttpStatusCode.RequestTimeout, true)]
    [DataRow(HttpStatusCode.TooManyRequests, true)]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task ExecuteAsync_RetriesOnlyTransientStatusCodes(HttpStatusCode? statusCode, bool expectRetry)
    {
        var policy = CreatePolicy(maxRetryAttempts: 1);
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => policy.ExecuteAsync<string>("op", _ =>
            {
                attempts++;
                throw new HttpRequestException("boom", null, statusCode);
            }, CancellationToken.None));

        Assert.AreEqual(expectRetry ? 2 : 1, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_IOException_IsRetried()
    {
        var policy = CreatePolicy(maxRetryAttempts: 1);
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<IOException>(
            () => policy.ExecuteAsync<string>("op", _ =>
            {
                attempts++;
                throw new IOException("disk");
            }, CancellationToken.None));

        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_Cancellation_Propagates()
    {
        var policy = CreatePolicy();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => policy.ExecuteAsync<string>("op", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return "unreachable";
            }, cts.Token));
    }
}
