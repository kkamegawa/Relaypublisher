using System.Net;
using System.Security.Cryptography;
using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class GitHubReleaseSourceProviderTests
{
    private const string Token = "ghp_super-secret-token";
    private const string SecretName = "GH_RELEASE_PAT";
    private const string ReleaseUrl = "https://api.github.com/repos/contoso/tools/releases/tags/v1.2.3";
    private const string AssetUrl = "https://api.github.com/repos/contoso/tools/releases/assets/42";

    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.3",
          "assets": [
            { "id": 41, "name": "other.zip" },
            { "id": 42, "name": "tool.exe" }
          ]
        }
        """;

    /// <summary>Maps absolute URL to a queue of responses and records every request sent.</summary>
    private sealed class RoutingStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Enqueue(string url, HttpStatusCode statusCode, byte[]? content = null)
            => EnqueueFactory(url, () => new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(content ?? []),
            });

        public void EnqueueFactory(string url, Func<HttpResponseMessage> factory)
        {
            if (!_routes.TryGetValue(url, out var queue))
            {
                queue = new Queue<Func<HttpResponseMessage>>();
                _routes[url] = queue;
            }

            queue.Enqueue(factory);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            if (!_routes.TryGetValue(url, out var queue) || queue.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"no stubbed response for {url}"),
                });
            }

            return Task.FromResult(queue.Dequeue()());
        }
    }

    private static GitHubReleaseSourceProvider CreateProvider(
        RoutingStubHandler handler, string? tokenValue = Token)
        => new(
            new HttpClient(handler),
            new DownloadRetryPolicy(
                new SourceRetryOptions { BaseRetryDelay = TimeSpan.Zero },
                NullLogger<DownloadRetryPolicy>.Instance),
            NullLogger<GitHubReleaseSourceProvider>.Instance,
            name => name == SecretName ? tokenValue : null);

    private static SourceManifest CreateSource(AuthManifest? auth) => new()
    {
        Type = "githubRelease",
        Owner = "contoso",
        Repository = "tools",
        Tag = "v1.2.3",
        AssetName = "tool.exe",
        Destination = "bin/tool.exe",
        Sha256 = new string('a', 64),
        Auth = auth,
    };

    private static AuthManifest TokenAuth() => new() { Type = "token", SecretName = SecretName };

    private static string CreateDestination()
        => Path.Combine(Path.GetTempPath(), $"gh-provider-test-{Guid.NewGuid():N}", "bin", "tool.exe");

    private static void Cleanup(string destination)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(destination))!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_TokenAuth_DownloadsAssetWithExpectedHeaders()
    {
        var content = "asset-bytes"u8.ToArray();
        var handler = new RoutingStubHandler();
        handler.Enqueue(ReleaseUrl, HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson));
        handler.Enqueue(AssetUrl, HttpStatusCode.OK, content);
        var provider = CreateProvider(handler);
        var destination = CreateDestination();

        try
        {
            var result = await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(TokenAuth()), destination), CancellationToken.None);

            Assert.AreEqual(content.Length, result.SizeBytes);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(content)), result.Sha256);
            Assert.HasCount(2, handler.Requests);

            var metadataRequest = handler.Requests[0];
            Assert.AreEqual($"Bearer {Token}", metadataRequest.Headers.Authorization?.ToString());
            Assert.AreEqual("application/vnd.github+json", metadataRequest.Headers.Accept.Single().MediaType);
            Assert.IsTrue(metadataRequest.Headers.UserAgent.Any());

            var assetRequest = handler.Requests[1];
            Assert.AreEqual($"Bearer {Token}", assetRequest.Headers.Authorization?.ToString());
            Assert.AreEqual("application/octet-stream", assetRequest.Headers.Accept.Single().MediaType);
            Assert.IsTrue(assetRequest.Headers.UserAgent.Any());
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_NoAuth_SendsNoAuthorizationHeader()
    {
        var handler = new RoutingStubHandler();
        handler.Enqueue(ReleaseUrl, HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson));
        handler.Enqueue(AssetUrl, HttpStatusCode.OK, "x"u8.ToArray());
        var provider = CreateProvider(handler);
        var destination = CreateDestination();

        try
        {
            await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(auth: null), destination), CancellationToken.None);

            Assert.IsTrue(handler.Requests.All(r => r.Headers.Authorization is null));
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_MissingEnvironmentVariable_FailsWithActionableMessage()
    {
        var provider = CreateProvider(new RoutingStubHandler(), tokenValue: null);

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(TokenAuth()), CreateDestination()), CancellationToken.None));

        Assert.Contains(SecretName, ex.Message);
        Assert.Contains("fork", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_EmptyEnvironmentVariable_FailsWithActionableMessage()
    {
        var provider = CreateProvider(new RoutingStubHandler(), tokenValue: "");

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(TokenAuth()), CreateDestination()), CancellationToken.None));

        Assert.Contains(SecretName, ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_WorkloadIdentityAuth_IsRejected()
    {
        var provider = CreateProvider(new RoutingStubHandler());
        var source = CreateSource(new AuthManifest { Type = "workloadIdentity" });

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(source, CreateDestination()), CancellationToken.None));

        Assert.Contains("workloadIdentity", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_AssetNameNotInRelease_ListsAvailableAssets()
    {
        var handler = new RoutingStubHandler();
        handler.Enqueue(ReleaseUrl, HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson));
        var provider = CreateProvider(handler);
        var source = CreateSource(TokenAuth());
        source.AssetName = "missing.exe";

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(source, CreateDestination()), CancellationToken.None));

        Assert.Contains("missing.exe", ex.Message);
        Assert.Contains("tool.exe", ex.Message);
        Assert.Contains("other.zip", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_ReleaseTagNotFound_MessageOmitsToken()
    {
        var handler = new RoutingStubHandler();
        handler.Enqueue(ReleaseUrl, HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(TokenAuth()), CreateDestination()), CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("contoso/tools@v1.2.3", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_TransientAssetFailure_IsRetried()
    {
        var content = "asset-bytes"u8.ToArray();
        var handler = new RoutingStubHandler();
        handler.Enqueue(ReleaseUrl, HttpStatusCode.OK, Encoding.UTF8.GetBytes(ReleaseJson));
        handler.Enqueue(AssetUrl, HttpStatusCode.ServiceUnavailable);
        handler.Enqueue(AssetUrl, HttpStatusCode.OK, content);
        var provider = CreateProvider(handler);
        var destination = CreateDestination();

        try
        {
            var result = await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(TokenAuth()), destination), CancellationToken.None);

            Assert.AreEqual(content.Length, result.SizeBytes);
            Assert.HasCount(3, handler.Requests);
        }
        finally
        {
            Cleanup(destination);
        }
    }
}
