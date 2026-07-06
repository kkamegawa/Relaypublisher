using System.Net;
using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class PublicHttpSourceProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, byte[] Content)> _responses;
        private (HttpStatusCode StatusCode, byte[] Content) _lastResponse;

        public StubHandler(params (HttpStatusCode StatusCode, byte[] Content)[] responses)
        {
            if (responses.Length == 0)
            {
                throw new ArgumentException("At least one response must be provided.", nameof(responses));
            }

            _responses = new Queue<(HttpStatusCode, byte[])>(responses);
            _lastResponse = responses[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (statusCode, content) = _responses.Count > 0 ? _responses.Dequeue() : _lastResponse;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(content),
            });
        }
    }

    private static PublicHttpSourceProvider CreateProvider(params (HttpStatusCode, byte[])[] responses)
        => new(
            new HttpClient(new StubHandler(responses)),
            new DownloadRetryPolicy(
                new SourceRetryOptions { BaseRetryDelay = TimeSpan.Zero },
                NullLogger<DownloadRetryPolicy>.Instance),
            NullLogger<PublicHttpSourceProvider>.Instance);

    private static PublicHttpSourceProvider CreateProvider(HttpStatusCode statusCode, byte[] content)
        => CreateProvider(responses: (statusCode, content));

    private static SourceManifest CreateSource(AuthManifest? auth = null) => new()
    {
        Type = "publicHttp",
        Url = "https://example.com/downloads/tool.exe",
        Destination = "bin/tool.exe",
        Sha256 = new string('a', 64),
        Auth = auth,
    };

    [TestMethod]
    public async Task DownloadAsync_Success_WritesFileAndComputesSha256()
    {
        var content = "binary-content"u8.ToArray();
        var provider = CreateProvider(HttpStatusCode.OK, content);
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}", "bin", "tool.exe");

        try
        {
            var result = await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(), destination), CancellationToken.None);

            Assert.IsTrue(File.Exists(destination));
            Assert.AreEqual(content.Length, result.SizeBytes);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(content)), result.Sha256);
        }
        finally
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(destination))!;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadAsync_HttpError_ThrowsSourceDownloadException()
    {
        var provider = CreateProvider(HttpStatusCode.NotFound, []);
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}.exe");

        await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(new SourceDownloadRequest(CreateSource(), destination), CancellationToken.None));
    }

    [TestMethod]
    public async Task DownloadAsync_HttpError_ExceptionMessageOmitsQueryString()
    {
        var provider = CreateProvider(HttpStatusCode.NotFound, []);
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}.exe");
        var source = CreateSource();
        source.Url = "https://example.com/downloads/tool.exe?sig=super-secret-token";

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(new SourceDownloadRequest(source, destination), CancellationToken.None));

        Assert.DoesNotContain("super-secret-token", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_TransientFailureThenSuccess_IsRetried()
    {
        var content = "binary-content"u8.ToArray();
        var provider = CreateProvider(
            (HttpStatusCode.ServiceUnavailable, []),
            (HttpStatusCode.OK, content));
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}", "bin", "tool.exe");

        try
        {
            var result = await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(), destination), CancellationToken.None);

            Assert.AreEqual(content.Length, result.SizeBytes);
        }
        finally
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(destination))!;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadAsync_TokenAuth_ThrowsSourceDownloadException()
    {
        var provider = CreateProvider(HttpStatusCode.OK, []);
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}.exe");
        var source = CreateSource(new AuthManifest { Type = "token", SecretName = "SOME_TOKEN" });

        await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(new SourceDownloadRequest(source, destination), CancellationToken.None));
    }
}
