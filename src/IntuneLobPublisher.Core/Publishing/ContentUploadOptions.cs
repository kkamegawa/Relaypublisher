namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Configuration for the Win32 content upload flow (doc/issues/issue-003-intune-graph-win32.md).</summary>
public sealed class ContentUploadOptions
{
    /// <summary>Size of each Azure Storage block blob chunk. Default 6 MiB.</summary>
    public int BlockSizeBytes { get; init; } = 6 * 1024 * 1024;

    /// <summary>How long to wait between polls while waiting for <c>azureStorageUriRequestSuccess</c>.</summary>
    public TimeSpan AzureStorageUriPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time to wait for <c>azureStorageUriRequestSuccess</c> before failing.</summary>
    public TimeSpan AzureStorageUriTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait between polls while waiting for <c>commitFileSuccess</c>.</summary>
    public TimeSpan CommitPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time to wait for <c>commitFileSuccess</c> before failing. Configurable per issue #13.</summary>
    public TimeSpan CommitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait between polls while waiting for <c>publishingState</c> to leave "processing".</summary>
    public TimeSpan PublishingStatePollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum time to wait for <c>publishingState</c> to become "published" before failing.</summary>
    public TimeSpan PublishingStateTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When the time remaining before the SAS URI's expiration drops below this margin, the uploader
    /// calls the Graph <c>renewUpload</c> action before staging the next block.
    /// </summary>
    public TimeSpan RenewalSafetyMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait before retrying a Storage block-blob call (stage or commit) against the *same*
    /// SAS URI after it was rejected with a 403 <c>AuthenticationFailed</c> that is not an expiry (issue #150:
    /// a SAS whose signed identifier points at a stored access policy that has not propagated yet also
    /// surfaces as this error, and Microsoft documents up to 30 seconds for propagation).
    /// </summary>
    public TimeSpan SasActivationRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Total time to keep retrying a same-SAS 403 before calling <c>renewUpload</c> (issue #150 "Phase A").
    /// Deliberately larger than the ~30 second stored-access-policy propagation window Microsoft documents,
    /// so a slow propagation is not mistaken for a genuinely rejected SAS.
    /// </summary>
    public TimeSpan SasActivationSameSasWindow { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Maximum number of <c>renewUpload</c> calls issued to recover from a 403 (issue #150 "Phase B"),
    /// separate from the pre-emptive expiry-driven renewals <see cref="RenewalSafetyMargin"/> already
    /// triggers. Kept small because if <c>renewUpload</c> itself re-creates the stored access policy, each
    /// call can re-arm the propagation window <see cref="SasActivationSameSasWindow"/> waits out, and
    /// retrying without bound would not converge.
    /// </summary>
    public int SasActivationMaxRenewals { get; init; } = 1;

    /// <summary>
    /// Upper bound on the whole SAS-authentication-403 recovery for a single stage/commit call (same-SAS
    /// retries plus any renewals), enforced by cancelling the recovery's own linked token - not a limit
    /// on the upload as a whole, and not extended when a renewal restarts the same-SAS wait.
    /// </summary>
    public TimeSpan SasActivationTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
