using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class AzureStorageBlockBlobUploaderTests
{
    private sealed record CapturedRequest(string Method, string Query);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri!.Query));

            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-08-04");
            response.Headers.TryAddWithoutValidation("Date", DateTimeOffset.UtcNow.ToString("R"));
            response.Headers.TryAddWithoutValidation("ETag", "\"etag-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }
    }

    private static readonly Uri SasUri = new("https://sasaccount.blob.core.windows.net/container/blob?sv=fake-sas");

    private static bool HasCompValue(string query, string value)
        => query.TrimStart('?').Split('&').Any(parameter => parameter == $"comp={value}");

    private static IAzureStorageBlockBlobUploader CreateUploader(RecordingHandler handler)
        => new AzureStorageBlockBlobUploader(clientOptions: new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) });

    private static Func<CancellationToken, Task<SasUriRenewal>> NoRenewalExpected()
        => _ => throw new InvalidOperationException("renewal should not have been requested");

    [TestMethod]
    public async Task UploadAsync_SmallContent_StagesOneBlockAndCommits()
    {
        var handler = new RecordingHandler();
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri, DateTimeOffset.UtcNow.AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, CancellationToken.None);

        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList());
        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "blocklist")).ToList());
    }

    [TestMethod]
    public async Task UploadAsync_ContentLargerThanBlockSize_StagesOneBlockPerChunk()
    {
        var handler = new RecordingHandler();
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());

        await uploader.UploadAsync(
            SasUri, DateTimeOffset.UtcNow.AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 3 }, CancellationToken.None);

        // 10 bytes split into 3-byte blocks -> 3, 3, 3, 1
        Assert.HasCount(4, handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList());
        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "blocklist")).ToList());
    }

    [TestMethod]
    public async Task UploadAsync_ExpiringWithinSafetyMargin_RequestsRenewalBeforeStagingBlocks()
    {
        var handler = new RecordingHandler();
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream([1, 2, 3, 4]);
        var renewCallCount = 0;
        var renewedUri = new Uri("https://sasaccount.blob.core.windows.net/container/blob?sv=renewed-sas");

        await uploader.UploadAsync(
            SasUri,
            DateTimeOffset.UtcNow.AddMinutes(1),
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(renewedUri, DateTimeOffset.UtcNow.AddHours(1)));
            },
            new ContentUploadOptions { BlockSizeBytes = 1024, RenewalSafetyMargin = TimeSpan.FromMinutes(5) },
            CancellationToken.None);

        Assert.AreEqual(1, renewCallCount);
    }

    [TestMethod]
    public async Task UploadAsync_ExpiryFarInFuture_DoesNotRequestRenewal()
    {
        var handler = new RecordingHandler();
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri,
            DateTimeOffset.UtcNow.AddHours(1),
            content,
            NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024, RenewalSafetyMargin = TimeSpan.FromMinutes(5) },
            CancellationToken.None);
    }

    [TestMethod]
    public async Task UploadAsync_NonPositiveBlockSize_ThrowsArgumentOutOfRangeException()
    {
        var handler = new RecordingHandler();
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream([1, 2, 3, 4]);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => uploader.UploadAsync(
            SasUri, DateTimeOffset.UtcNow.AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 0 }, CancellationToken.None));
    }
}
