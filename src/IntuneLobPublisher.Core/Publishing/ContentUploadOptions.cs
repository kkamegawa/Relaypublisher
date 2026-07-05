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
}
