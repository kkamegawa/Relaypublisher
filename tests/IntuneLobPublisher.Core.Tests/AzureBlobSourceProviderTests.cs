using System.Security.Cryptography;
using Azure;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class AzureBlobSourceProviderTests
{
    private sealed class FakeBlobDownloader : IAzureBlobDownloader
    {
        private readonly byte[]? _content;
        private readonly Exception? _exception;

        public FakeBlobDownloader(byte[] content) => _content = content;

        public FakeBlobDownloader(Exception exception) => _exception = exception;

        public (string AccountName, string Container, string BlobName, string DestinationPath)? LastCall
        {
            get;
            private set;
        }

        public Task DownloadToAsync(
            string accountName, string container, string blobName, string destinationPath,
            CancellationToken cancellationToken)
        {
            LastCall = (accountName, container, blobName, destinationPath);
            if (_exception is not null)
            {
                throw _exception;
            }

            return File.WriteAllBytesAsync(destinationPath, _content!, cancellationToken);
        }
    }

    private static AzureBlobSourceProvider CreateProvider(IAzureBlobDownloader downloader)
        => new(downloader, NullLogger<AzureBlobSourceProvider>.Instance);

    private static SourceManifest CreateSource(AuthManifest? auth) => new()
    {
        Type = "azureBlob",
        AccountName = "contosopackages",
        Container = "intune-packages",
        BlobName = "macos/contoso-tool/1.2.3/tool.pkg",
        Destination = "tool.pkg",
        Sha256 = new string('a', 64),
        Auth = auth,
    };

    private static AuthManifest WorkloadIdentityAuth() => new() { Type = "workloadIdentity" };

    private static string CreateDestination()
        => Path.Combine(Path.GetTempPath(), $"blob-provider-test-{Guid.NewGuid():N}", "bin", "tool.pkg");

    private static void Cleanup(string destination)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(destination))!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_WorkloadIdentity_DownloadsAndComputesSha256()
    {
        var content = "pkg-bytes"u8.ToArray();
        var downloader = new FakeBlobDownloader(content);
        var provider = CreateProvider(downloader);
        var destination = CreateDestination();

        try
        {
            var result = await provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(WorkloadIdentityAuth()), destination), CancellationToken.None);

            Assert.AreEqual(content.Length, result.SizeBytes);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(content)), result.Sha256);
            Assert.AreEqual(
                ("contosopackages", "intune-packages", "macos/contoso-tool/1.2.3/tool.pkg", destination),
                downloader.LastCall);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_NoAuth_IsRejectedWithPublicHttpHint()
    {
        var provider = CreateProvider(new FakeBlobDownloader([]));

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(CreateSource(auth: null), CreateDestination()), CancellationToken.None));

        Assert.Contains("workloadIdentity", ex.Message);
        Assert.Contains("publicHttp", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_TokenAuth_IsRejected()
    {
        var provider = CreateProvider(new FakeBlobDownloader([]));
        var source = CreateSource(new AuthManifest { Type = "token", SecretName = "SOME_TOKEN" });

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(source, CreateDestination()), CancellationToken.None));

        Assert.Contains("token", ex.Message);
    }

    [TestMethod]
    public async Task DownloadAsync_Forbidden_WrapsWithRoleHintAndNoSecrets()
    {
        var provider = CreateProvider(new FakeBlobDownloader(
            new RequestFailedException(403, "Forbidden", "AuthorizationPermissionMismatch", null)));
        var destination = CreateDestination();

        try
        {
            var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
                () => provider.DownloadAsync(
                    new SourceDownloadRequest(CreateSource(WorkloadIdentityAuth()), destination), CancellationToken.None));

            Assert.Contains("Storage Blob Data Reader", ex.Message);
            Assert.Contains("contosopackages", ex.Message);
            Assert.Contains("intune-packages", ex.Message);
            Assert.DoesNotContain("sig=", ex.Message);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_NotFound_WrapsWithPathHint()
    {
        var provider = CreateProvider(new FakeBlobDownloader(
            new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null)));
        var destination = CreateDestination();

        try
        {
            var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
                () => provider.DownloadAsync(
                    new SourceDownloadRequest(CreateSource(WorkloadIdentityAuth()), destination), CancellationToken.None));

            Assert.Contains("status 404", ex.Message);
            Assert.Contains("BlobNotFound", ex.Message);
        }
        finally
        {
            Cleanup(destination);
        }
    }

    [TestMethod]
    public async Task DownloadAsync_MissingFields_Fails()
    {
        var provider = CreateProvider(new FakeBlobDownloader([]));
        var source = CreateSource(WorkloadIdentityAuth());
        source.Container = null;

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => provider.DownloadAsync(
                new SourceDownloadRequest(source, CreateDestination()), CancellationToken.None));

        Assert.Contains("Container", ex.Message);
    }
}
