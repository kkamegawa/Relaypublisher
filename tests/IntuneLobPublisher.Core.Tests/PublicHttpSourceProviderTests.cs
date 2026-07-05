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
        private readonly HttpStatusCode _statusCode;
        private readonly byte[] _content;

        public StubHandler(HttpStatusCode statusCode, byte[] content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new ByteArrayContent(_content),
            });
    }

    private static PublicHttpSourceProvider CreateProvider(HttpStatusCode statusCode, byte[] content)
        => new(
            new HttpClient(new StubHandler(statusCode, content)),
            NullLogger<PublicHttpSourceProvider>.Instance);

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
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(destination))!, recursive: true);
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
    public async Task DownloadAsync_TokenAuth_ThrowsSourceDownloadException()
    {
        var provider = CreateProvider(HttpStatusCode.OK, []);
        var destination = Path.Combine(Path.GetTempPath(), $"provider-test-{Guid.NewGuid():N}.exe");
        var source = CreateSource(new AuthManifest { Type = "token", SecretName = "SOME_TOKEN" });

        await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(new SourceDownloadRequest(source, destination), CancellationToken.None));
    }
}
