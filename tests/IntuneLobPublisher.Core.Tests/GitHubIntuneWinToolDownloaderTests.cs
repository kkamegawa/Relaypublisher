using System.Net;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class GitHubIntuneWinToolDownloaderTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(25, cancellationToken);
            return LatestReleaseResponse();
        }
    }

    private sealed class FailureThenSuccessHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("rate limit exceeded"),
                });
            }

            return Task.FromResult(LatestReleaseResponse());
        }
    }

    private sealed class CancellationThenSuccessHandler : HttpMessageHandler
    {
        private int _requestCount;

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                FirstRequestStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return LatestReleaseResponse();
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_RepeatedAndConcurrentCalls_QueryOnlyOnce()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var downloader = CreateDownloader(httpClient);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => downloader.GetLatestVersionAsync(CancellationToken.None)));
        var subsequentResult = await downloader.GetLatestVersionAsync(CancellationToken.None);

        Assert.IsTrue(results.All(version => version == "v1.8.7"));
        Assert.AreEqual("v1.8.7", subsequentResult);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_FailedRequestIsNotCachedAndCanBeRetried()
    {
        var handler = new FailureThenSuccessHandler();
        using var httpClient = new HttpClient(handler);
        var downloader = CreateDownloader(httpClient);

        await Assert.ThrowsExactlyAsync<PackagingException>(
            () => downloader.GetLatestVersionAsync(CancellationToken.None));
        var version = await downloader.GetLatestVersionAsync(CancellationToken.None);

        Assert.AreEqual("v1.8.7", version);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_CancelledRequestIsNotCachedAndCanBeRetried()
    {
        var handler = new CancellationThenSuccessHandler();
        using var httpClient = new HttpClient(handler);
        var downloader = CreateDownloader(httpClient);
        using var cancellationSource = new CancellationTokenSource();

        var cancelledRequest = downloader.GetLatestVersionAsync(cancellationSource.Token);
        await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelledRequest);
        var version = await downloader.GetLatestVersionAsync(CancellationToken.None);

        Assert.AreEqual("v1.8.7", version);
        Assert.AreEqual(2, handler.RequestCount);
    }

    private static GitHubIntuneWinToolDownloader CreateDownloader(HttpClient httpClient)
        => new(httpClient, NullLogger<GitHubIntuneWinToolDownloader>.Instance);

    private static HttpResponseMessage LatestReleaseResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tag_name":"v1.8.7"}"""),
        };
}
