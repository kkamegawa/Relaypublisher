using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class AzureStorageBlockBlobUploaderTests
{
    private sealed record CapturedRequest(string Method, string Query, byte[] Body);

    /// <summary>
    /// Fakes the Azure Storage HTTP endpoint. By default every request succeeds (201 Created); a test can
    /// pass <paramref name="respond"/> to script specific responses (e.g. a 403 <c>AuthenticationFailed</c>)
    /// per call, keyed off the request already captured in <see cref="Requests"/>.
    /// </summary>
    private sealed class RecordingHandler(Func<CapturedRequest, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var captured = new CapturedRequest(request.Method.Method, request.RequestUri!.Query, body);
            Requests.Add(captured);

            if (respond is not null)
            {
                return respond(captured);
            }

            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
            response.Headers.TryAddWithoutValidation("x-ms-version", "2024-08-04");
            response.Headers.TryAddWithoutValidation("Date", DateTimeOffset.UtcNow.ToString("R"));
            response.Headers.TryAddWithoutValidation("ETag", "\"etag-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return response;
        }
    }

    /// <summary>Fixed clock a test can advance explicitly, standing in for wall-clock time elapsed during a real network call.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    /// <summary>Captures every message logged, for the non-leakage assertions (issue #150).</summary>
    private sealed class RecordingLogger : ILogger<AzureStorageBlockBlobUploader>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }

    private static readonly Uri SasUri = new("https://sasaccount.blob.core.windows.net/container/blob?sv=fake-sas&si=policy-1&sig=AAA");
    private static readonly Uri RenewedSasUri = new("https://sasaccount.blob.core.windows.net/container/blob?sv=renewed-sas&si=policy-2&sig=BBB");

    private static bool HasCompValue(string query, string value)
        => query.TrimStart('?').Split('&').Any(parameter => parameter == $"comp={value}");

    private static IAzureStorageBlockBlobUploader CreateUploader(
        RecordingHandler handler,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ILogger<AzureStorageBlockBlobUploader>? logger = null)
        => new AzureStorageBlockBlobUploader(
            timeProvider,
            clientOptions: new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) },
            delayAsync,
            logger);

    private static Func<CancellationToken, Task<SasUriRenewal>> NoRenewalExpected()
        => _ => throw new InvalidOperationException("renewal should not have been requested");

    private static HttpResponseMessage Create403AuthenticationFailedResponse(string requestId = "req-403", string errorCode = "AuthenticationFailed")
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error>" +
            $"<Code>{errorCode}</Code>" +
            $"<Message>Server failed to authenticate the request.\nRequestId:{requestId}\nTime:{DateTimeOffset.UtcNow:O}</Message>" +
            "<AuthenticationErrorDetail>SAS identifier cannot be found for specified signed identifier</AuthenticationErrorDetail>" +
            "</Error>";
        var response = new HttpResponseMessage((HttpStatusCode)403)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-request-id", requestId);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", errorCode);
        response.Headers.TryAddWithoutValidation("x-ms-version", "2024-08-04");
        response.Headers.TryAddWithoutValidation("Date", DateTimeOffset.UtcNow.ToString("R"));
        return response;
    }

    private static HttpResponseMessage Create404BlobNotFoundResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("<?xml version=\"1.0\"?><Error><Code>BlobNotFound</Code></Error>", Encoding.UTF8, "application/xml"),
        };
        response.Headers.TryAddWithoutValidation("x-ms-error-code", "BlobNotFound");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "req-404");
        return response;
    }

    private static HttpResponseMessage Success()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
        response.Headers.TryAddWithoutValidation("Date", DateTimeOffset.UtcNow.ToString("R"));
        return response;
    }

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

    [TestMethod]
    public async Task UploadAsync_ExpiresDuringFinalBlockUpload_RenewsBeforeCommittingInsteadOfFailing()
    {
        // Regression test: the renewal check used to run only before staging each block, never before
        // the final Put Block List call. A single-block upload whose SAS crosses the safety margin while
        // that one StageBlockAsync call is in flight used to reach CommitBlockListAsync with an
        // already-expired SAS and no chance to renew.
        var timeProvider = new ManualTimeProvider();
        var renewCallCount = 0;
        var renewedUri = new Uri("https://sasaccount.blob.core.windows.net/container/blob?sv=renewed-sas");
        var handler = new RecordingHandler(request =>
        {
            if (HasCompValue(request.Query, "block"))
            {
                // Simulate the staging call itself taking long enough to cross the safety margin,
                // without needing renewal at the point the block was staged.
                timeProvider.Advance(TimeSpan.FromMinutes(4));
            }

            return Success();
        });
        var uploader = CreateUploader(handler, timeProvider);
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddMinutes(5),
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(renewedUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions { BlockSizeBytes = 1024, RenewalSafetyMargin = TimeSpan.FromMinutes(2) },
            CancellationToken.None);

        Assert.AreEqual(1, renewCallCount);
        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList());
        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "blocklist")).ToList());
    }

    // --- issue #150: SAS-authentication-403 recovery ---

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_RetriesSameSasAndSucceeds()
    {
        var timeProvider = new ManualTimeProvider();
        var callCount = 0;
        var handler = new RecordingHandler(request =>
        {
            callCount++;
            return callCount == 1 ? Create403AuthenticationFailedResponse() : Success();
        });
        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri, timeProvider.GetUtcNow().AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, CancellationToken.None);

        Assert.HasCount(2, handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList());
        Assert.HasCount(1, handler.Requests.Where(r => HasCompValue(r.Query, "blocklist")).ToList());
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_RetriedRequestBodyIsByteIdentical()
    {
        // Regression test: StageBlockAsync used to be called with a MemoryStream shared across attempts.
        // Its read position had already advanced past the failed attempt's send, so a retry using the
        // same instance would stage an empty or truncated block instead of the original bytes.
        var timeProvider = new ManualTimeProvider();
        var callCount = 0;
        var handler = new RecordingHandler(request =>
        {
            callCount++;
            return callCount == 1 ? Create403AuthenticationFailedResponse() : Success();
        });
        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        byte[] payload = [10, 20, 30, 40, 50];
        using var content = new MemoryStream(payload);

        await uploader.UploadAsync(
            SasUri, timeProvider.GetUtcNow().AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, CancellationToken.None);

        var blockRequests = handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList();
        Assert.HasCount(2, blockRequests);
        CollectionAssert.AreEqual(payload, blockRequests[0].Body);
        CollectionAssert.AreEqual(payload, blockRequests[1].Body);
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_SameSasWindowExceeded_RenewsThenSucceeds()
    {
        var timeProvider = new ManualTimeProvider();
        var renewCallCount = 0;
        var handler = new RecordingHandler(request =>
        {
            // The original SAS (sv=fake-sas) never recovers; only the renewed SAS (sv=renewed-sas) succeeds.
            return request.Query.Contains("sv=renewed-sas", StringComparison.Ordinal)
                ? Success()
                : Create403AuthenticationFailedResponse();
        });
        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2, 3, 4]);
        var expiresAt = timeProvider.GetUtcNow().AddHours(1);

        await uploader.UploadAsync(
            SasUri,
            expiresAt,
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(RenewedSasUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                SasActivationRetryDelay = TimeSpan.FromSeconds(10),
                SasActivationSameSasWindow = TimeSpan.FromSeconds(45),
                SasActivationMaxRenewals = 1,
            },
            CancellationToken.None);

        Assert.AreEqual(1, renewCallCount);
        var blockRequests = handler.Requests.Where(r => HasCompValue(r.Query, "block")).ToList();
        Assert.IsTrue(blockRequests[^1].Query.Contains("sv=renewed-sas", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_RetryDelayLongerThanSameSasWindow_DoesNotOvershootWindow()
    {
        // Regression test (Copilot review, PR #151): the same-SAS phase used to always wait the full
        // SasActivationRetryDelay before checking whether SasActivationSameSasWindow had elapsed, so a
        // retry delay longer than (or close to) the window could overshoot it by up to one full delay.
        var timeProvider = new ManualTimeProvider();
        var start = timeProvider.GetUtcNow();
        var recordedDelays = new List<TimeSpan>();
        DateTimeOffset? renewedAt = null;
        var handler = new RecordingHandler(request =>
            request.Query.Contains("sv=renewed-sas", StringComparison.Ordinal) ? Success() : Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler,
            timeProvider,
            delayAsync: (delay, _) =>
            {
                recordedDelays.Add(delay);
                timeProvider.Advance(delay);
                return Task.CompletedTask;
            });
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri,
            start.AddHours(1),
            content,
            _ =>
            {
                renewedAt = timeProvider.GetUtcNow();
                return Task.FromResult(new SasUriRenewal(RenewedSasUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                SasActivationRetryDelay = TimeSpan.FromSeconds(20),
                SasActivationSameSasWindow = TimeSpan.FromSeconds(5),
                SasActivationMaxRenewals = 1,
            },
            CancellationToken.None);

        // The renewal must happen exactly when the window elapses (t=5s), not after a full retry delay
        // (t=20s), and no single wait may exceed the window.
        Assert.AreEqual(start + TimeSpan.FromSeconds(5), renewedAt);
        Assert.IsTrue(recordedDelays.All(d => d <= TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_RenewalLimitExceeded_ThrowsWithoutLivelocking()
    {
        var timeProvider = new ManualTimeProvider();
        var renewCallCount = 0;
        var handler = new RecordingHandler(_ => Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2, 3, 4]);

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadRejectedException>(() => uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddHours(1),
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(RenewedSasUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                SasActivationRetryDelay = TimeSpan.FromSeconds(10),
                SasActivationSameSasWindow = TimeSpan.FromSeconds(45),
                SasActivationMaxRenewals = 1,
                SasActivationTimeout = TimeSpan.FromMinutes(30),
            },
            CancellationToken.None));

        // Bounded by SasActivationMaxRenewals, not by SasActivationTimeout: this must fail promptly
        // (advancing only the fake clock) rather than needing the whole 30-minute deadline to elapse.
        Assert.AreEqual("stageBlock", ex.Stage);
        Assert.AreEqual(403, ex.Status);
        Assert.AreEqual("AuthenticationFailed", ex.ErrorCode);
        Assert.AreEqual(1, renewCallCount);
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_NearExpiry_SkipsSameSasRetryAndRenewsImmediately()
    {
        var timeProvider = new ManualTimeProvider();
        var renewCallCount = 0;
        var handler = new RecordingHandler(request =>
            request.Query.Contains("sv=renewed-sas", StringComparison.Ordinal) ? Success() : Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler,
            timeProvider,
            delayAsync: (_, _) => throw new InvalidOperationException("same-SAS retry delay should not run when the SAS is already near expiry"));
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddSeconds(30), // inside the default 2-minute RenewalSafetyMargin
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(RenewedSasUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions { BlockSizeBytes = 1024, SasActivationMaxRenewals = 1 },
            CancellationToken.None);

        Assert.AreEqual(1, renewCallCount);
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_ExpiresDuringSameSasWait_SwitchesToRenewal()
    {
        var timeProvider = new ManualTimeProvider();
        var renewCallCount = 0;
        var handler = new RecordingHandler(request =>
            request.Query.Contains("sv=renewed-sas", StringComparison.Ordinal) ? Success() : Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler,
            timeProvider,
            // The delay itself carries the fake clock past RenewalSafetyMargin, mid same-SAS retry.
            delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddMinutes(3),
            content,
            _ =>
            {
                renewCallCount++;
                return Task.FromResult(new SasUriRenewal(RenewedSasUri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                RenewalSafetyMargin = TimeSpan.FromMinutes(2),
                SasActivationRetryDelay = TimeSpan.FromMinutes(2), // one retry wait alone crosses the margin
                SasActivationSameSasWindow = TimeSpan.FromMinutes(30),
                SasActivationMaxRenewals = 1,
            },
            CancellationToken.None);

        Assert.AreEqual(1, renewCallCount);
    }

    [TestMethod]
    public async Task UploadAsync_PreventiveRenewalThenRecoveryRenewal_BothRenewalsHappen()
    {
        // RenewIfNeededAsync's expiry-driven ("preventive") renewals are a separate code path from issue
        // #150's 403-recovery renewals, with no shared counter: a preventive renewal earlier in the same
        // upload must not leave zero recovery budget for an authentication failure on a later block.
        var timeProvider = new ManualTimeProvider();
        var renewalKinds = new List<string>();
        var preventiveUri = new Uri("https://sasaccount.blob.core.windows.net/container/blob?sv=preventive-sas");
        var recoveredUri = new Uri("https://sasaccount.blob.core.windows.net/container/blob?sv=recovered-sas");
        var preventiveSasSuccessesRemaining = 1;

        var handler = new RecordingHandler(request =>
        {
            if (!HasCompValue(request.Query, "block"))
            {
                return Success();
            }

            if (request.Query.Contains("sv=recovered-sas", StringComparison.Ordinal))
            {
                return Success();
            }

            if (request.Query.Contains("sv=preventive-sas", StringComparison.Ordinal))
            {
                // Block 1 (the only caller while a success is still available) succeeds; block 2 finds
                // none left and must fail, forcing recovery instead of a second preventive renewal.
                if (preventiveSasSuccessesRemaining > 0)
                {
                    preventiveSasSuccessesRemaining--;
                    return Success();
                }

                return Create403AuthenticationFailedResponse();
            }

            throw new InvalidOperationException("staged on the original SAS despite being within RenewalSafetyMargin");
        });

        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2]); // two 1-byte blocks

        await uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddMinutes(1), // within the default 2-minute RenewalSafetyMargin
            content,
            _ =>
            {
                var kind = renewalKinds.Count == 0 ? "preventive" : "recovery";
                renewalKinds.Add(kind);
                var uri = kind == "preventive" ? preventiveUri : recoveredUri;
                return Task.FromResult(new SasUriRenewal(uri, timeProvider.GetUtcNow().AddHours(1)));
            },
            new ContentUploadOptions { BlockSizeBytes = 1, SasActivationMaxRenewals = 1 },
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "preventive", "recovery" }, renewalKinds);
    }

    [TestMethod]
    public async Task UploadAsync_NonAuthenticationRequestFailed_DoesNotRetry()
    {
        var handler = new RecordingHandler(_ => Create404BlobNotFoundResponse());
        var uploader = CreateUploader(handler);
        using var content = new MemoryStream([1, 2, 3, 4]);

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadRejectedException>(() => uploader.UploadAsync(
            SasUri, DateTimeOffset.UtcNow.AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, CancellationToken.None));

        Assert.AreEqual(404, ex.Status);
        Assert.AreEqual("BlobNotFound", ex.ErrorCode);
        Assert.HasCount(1, handler.Requests); // no retry at all
    }

    [TestMethod]
    public async Task UploadAsync_CommitBlockList_SasAuthenticationFailure_RetriesAndSucceeds()
    {
        var timeProvider = new ManualTimeProvider();
        var commitCalls = 0;
        var handler = new RecordingHandler(request =>
        {
            if (!HasCompValue(request.Query, "blocklist"))
            {
                return Success();
            }

            commitCalls++;
            return commitCalls == 1 ? Create403AuthenticationFailedResponse() : Success();
        });
        var uploader = CreateUploader(
            handler, timeProvider, delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; });
        using var content = new MemoryStream([1, 2, 3, 4]);

        await uploader.UploadAsync(
            SasUri, timeProvider.GetUtcNow().AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, CancellationToken.None);

        var commitRequests = handler.Requests.Where(r => HasCompValue(r.Query, "blocklist")).ToList();
        Assert.HasCount(2, commitRequests);
        // No duplicate block ids in either attempt's block list body.
        var firstBody = Encoding.UTF8.GetString(commitRequests[0].Body);
        var blockIdOccurrences = firstBody.Split("<Latest>").Length - 1;
        Assert.AreEqual(1, blockIdOccurrences);
    }

    [TestMethod]
    public async Task UploadAsync_CallerCancellation_DuringRecovery_PropagatesAsCancellationNotRejection()
    {
        var timeProvider = new ManualTimeProvider();
        using var callerCts = new CancellationTokenSource();
        var handler = new RecordingHandler(_ => Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler,
            timeProvider,
            delayAsync: (_, _) =>
            {
                // Simulate the caller cancelling the whole publish (e.g. Ctrl+C) while recovery is
                // waiting between same-SAS retries; recovery must tell this apart from its own deadline.
                callerCts.Cancel();
                throw new OperationCanceledException(callerCts.Token);
            });
        using var content = new MemoryStream([1, 2, 3, 4]);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => uploader.UploadAsync(
            SasUri, timeProvider.GetUtcNow().AddHours(1), content, NoRenewalExpected(),
            new ContentUploadOptions { BlockSizeBytes = 1024 }, callerCts.Token));
    }

    [TestMethod]
    public async Task UploadAsync_RecoveryDeadlineElapses_ThrowsRejectedNotCancellation()
    {
        // Unlike the other recovery tests, this one exercises the real CancellationTokenSource(TimeSpan,
        // TimeProvider) deadline itself, which - unlike SasActivationSameSasWindow/RenewalSafetyMargin
        // comparisons - is driven by a real OS timer and does not observe ManualTimeProvider.Advance().
        // A short real SasActivationTimeout keeps this test fast (well under a second) without needing a
        // fake timer implementation.
        var handler = new RecordingHandler(_ => Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(handler); // TimeProvider.System + real Task.Delay
        using var content = new MemoryStream([1, 2, 3, 4]);

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadRejectedException>(() => uploader.UploadAsync(
            SasUri,
            DateTimeOffset.UtcNow.AddHours(1),
            content,
            NoRenewalExpected(), // the same-SAS window (30s) far exceeds the 150ms deadline, so no renewal is expected
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                SasActivationRetryDelay = TimeSpan.FromMilliseconds(10),
                SasActivationSameSasWindow = TimeSpan.FromSeconds(30),
                SasActivationTimeout = TimeSpan.FromMilliseconds(150),
            },
            CancellationToken.None));

        Assert.AreEqual("stageBlock", ex.Stage);
    }

    [TestMethod]
    public async Task UploadAsync_SasAuthenticationFailure_ExceptionAndLoggingNeverContainSasUriOrSignature()
    {
        var timeProvider = new ManualTimeProvider();
        var recordingLogger = new RecordingLogger();
        var handler = new RecordingHandler(_ => Create403AuthenticationFailedResponse());
        var uploader = CreateUploader(
            handler,
            timeProvider,
            delayAsync: (delay, _) => { timeProvider.Advance(delay); return Task.CompletedTask; },
            logger: recordingLogger);
        using var content = new MemoryStream([1, 2, 3, 4]);

        var ex = await Assert.ThrowsExactlyAsync<ContentUploadRejectedException>(() => uploader.UploadAsync(
            SasUri,
            timeProvider.GetUtcNow().AddHours(1),
            content,
            NoRenewalExpected(),
            new ContentUploadOptions
            {
                BlockSizeBytes = 1024,
                SasActivationSameSasWindow = TimeSpan.FromSeconds(1),
                SasActivationRetryDelay = TimeSpan.FromSeconds(1),
                SasActivationMaxRenewals = 0,
            },
            CancellationToken.None));

        AssertNoSasLeak(ex.Message);
        AssertNoSasLeak(ex.ToString());
        foreach (var message in recordingLogger.Messages)
        {
            AssertNoSasLeak(message);
        }
    }

    private static void AssertNoSasLeak(string text)
    {
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("sig="));
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("si="));
        Assert.IsFalse(text.Contains(SasUri.ToString(), StringComparison.Ordinal));
    }
}
