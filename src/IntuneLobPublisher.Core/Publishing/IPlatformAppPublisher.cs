using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The platform-specific half of the publish flow: mapping a manifest entry to its Graph app resource
/// and publishing its content. <see cref="PublishOrchestrator"/> owns everything platform-neutral
/// (resolve, version guard, management metadata, assignment plan/apply) and dispatches to one of these
/// per <c>AppManifest.Platform</c> (<see cref="WindowsAppPublisher"/>, <see cref="MacOsAppPublisher"/>).
/// </summary>
public interface IPlatformAppPublisher
{
    /// <summary>
    /// Maps the manifest to its Graph payload and discards the result, so mapping errors (unknown
    /// Windows release / macOS version, unsupported icon format) surface in dry-run too, without
    /// making any Graph call.
    /// </summary>
    Task EnsureMappableAsync(PublishRequest request, CancellationToken cancellationToken);

    /// <summary>Maps the manifest (with <paramref name="notes"/> so a brand-new app is never metadata-less) and creates the app. Returns the created app id.</summary>
    Task<string> CreateAppAsync(PublishRequest request, string notes, CancellationToken cancellationToken);

    /// <summary>Maps the manifest and patches the existing app identified by <paramref name="appId"/>.</summary>
    Task UpdateAppAsync(string appId, PublishRequest request, CancellationToken cancellationToken);

    /// <summary>Uploads <paramref name="artifacts"/>' content to the app, using the platform's content extractor and Graph API version.</summary>
    Task<ContentUploadResult> PublishContentAsync(
        string appId,
        PublishRequest request,
        PackageArtifacts artifacts,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        CancellationToken cancellationToken);
}
