namespace IntuneLobPublisher.Core.Exceptions;

/// <summary>Base type for all publisher errors so the CLI can map them to exit codes uniformly.</summary>
public abstract class PublisherException : Exception
{
    protected PublisherException(string message)
        : base(message)
    {
    }

    protected PublisherException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest file could not be read or parsed.</summary>
public sealed class ManifestLoadException : PublisherException
{
    public ManifestLoadException(string message)
        : base(message)
    {
    }

    public ManifestLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest was parsed but failed validation.</summary>
public sealed class ManifestValidationException : PublisherException
{
    public ManifestValidationException(string message, IReadOnlyList<string> errors)
        : base(message)
    {
        Errors = errors;
    }

    /// <summary>Individual validation error messages including the failing field path.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Staging a package failed (missing file, copy failure, missing setup file, ...).</summary>
public sealed class StagingException : PublisherException
{
    public StagingException(string message)
        : base(message)
    {
    }

    public StagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Downloading an external source failed.</summary>
public sealed class SourceDownloadException : PublisherException
{
    public SourceDownloadException(string message)
        : base(message)
    {
    }

    public SourceDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A downloaded or staged file did not match its expected SHA256.</summary>
public sealed class ChecksumMismatchException : PublisherException
{
    public ChecksumMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>.intunewin generation failed (tool missing, tool download failed, non-zero exit, missing output).</summary>
public sealed class PackagingException : PublisherException
{
    public PackagingException(string message)
        : base(message)
    {
    }

    public PackagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest-supplied path is absolute or escapes its allowed root.</summary>
public sealed class UnsafePathException : PublisherException
{
    public UnsafePathException(string message)
        : base(message)
    {
    }
}

/// <summary>The acquired Graph token's `tid` claim does not match `--expected-tenant`. Thrown before any write.</summary>
public sealed class TenantMismatchException : PublisherException
{
    public TenantMismatchException(string expectedTenantId, string actualTenantId)
        : base($"Graph token tenant '{actualTenantId}' does not match --expected-tenant '{expectedTenantId}'.")
    {
        ExpectedTenantId = expectedTenantId;
        ActualTenantId = actualTenantId;
    }

    public string ExpectedTenantId { get; }

    public string ActualTenantId { get; }
}

/// <summary>
/// App resolution matched more than one Intune app for the same identity or DisplayName.
/// Thrown before any write so an ambiguous match never overwrites the wrong app.
/// </summary>
public sealed class AmbiguousAppMatchException : PublisherException
{
    public AmbiguousAppMatchException(string message, IReadOnlyList<string> matchedAppIds)
        : base(message)
    {
        MatchedAppIds = matchedAppIds;
    }

    /// <summary>Ids of every Intune app that matched.</summary>
    public IReadOnlyList<string> MatchedAppIds { get; }
}

/// <summary>The serialized management metadata JSON would not fit in the Intune `notes` field.</summary>
public sealed class ManagementMetadataTooLargeException : PublisherException
{
    public ManagementMetadataTooLargeException(int actualLength, int maxLength)
        : base($"Management metadata JSON is {actualLength} characters, which exceeds the {maxLength} character notes limit.")
    {
        ActualLength = actualLength;
        MaxLength = maxLength;
    }

    public int ActualLength { get; }

    public int MaxLength { get; }
}

/// <summary>
/// A manifest's <c>Requirements.MinimumOSVersion</c> build number has no known mapping to a Graph
/// <c>minimumSupportedWindowsRelease</c> value. Thrown instead of guessing so an unrecognized build fails fast.
/// </summary>
public sealed class UnsupportedWindowsBuildException : PublisherException
{
    public UnsupportedWindowsBuildException(string minimumOsVersion)
        : base($"MinimumOSVersion '{minimumOsVersion}' has no known minimumSupportedWindowsRelease mapping.")
    {
        MinimumOsVersion = minimumOsVersion;
    }

    public string MinimumOsVersion { get; }
}

/// <summary>A manifest's <c>Icon</c> file extension has no known Graph <c>largeIcon</c> MIME type mapping.</summary>
public sealed class UnsupportedIconFormatException : PublisherException
{
    public UnsupportedIconFormatException(string iconPath)
        : base($"Icon '{iconPath}' has an unsupported file extension. Supported: .png, .jpg, .jpeg.")
    {
        IconPath = iconPath;
    }

    public string IconPath { get; }
}

/// <summary>
/// Graph reported an explicit failure state for a content upload step (e.g. <c>azureStorageUriRequestFailed</c>,
/// <c>commitFileFailed</c>), as opposed to a timeout while waiting for a state to be reached.
/// </summary>
public sealed class ContentUploadFailedException : PublisherException
{
    public ContentUploadFailedException(string stage, string uploadState)
        : base($"Content upload step '{stage}' failed with Graph uploadState '{uploadState}'.")
    {
        Stage = stage;
        UploadState = uploadState;
    }

    /// <summary>The content upload step being waited on, e.g. "azureStorageUriRequest" or "commit".</summary>
    public string Stage { get; }

    /// <summary>The Graph <c>mobileAppContentFileUploadState</c> value that caused the failure.</summary>
    public string UploadState { get; }
}

/// <summary>Polling for a content upload step (SAS URI issuance, commit, publishing state) exceeded its configured timeout.</summary>
public sealed class ContentUploadTimedOutException : PublisherException
{
    public ContentUploadTimedOutException(string stage, TimeSpan timeout)
        : base($"Content upload step '{stage}' did not complete within {timeout}.")
    {
        Stage = stage;
        Timeout = timeout;
    }

    /// <summary>The content upload step being waited on, e.g. "azureStorageUriRequest", "commit" or "publishingState".</summary>
    public string Stage { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>
/// Assignment plan computation received input it cannot plan against: malformed manifest values
/// that slipped past validation, duplicate targets on the Intune side, or an intent the app type
/// does not support. Thrown before any write.
/// </summary>
public sealed class AssignmentPlanningException : PublisherException
{
    public AssignmentPlanningException(string message)
        : base(message)
    {
    }
}

/// <summary>Writing the publish result JSON file failed before or after publishing entries.</summary>
public sealed class PublishResultOutputException : PublisherException
{
    public PublishResultOutputException(string message)
        : base(message)
    {
    }

    public PublishResultOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A Microsoft Graph call failed after retries. Carries the request ids Graph returns for support/troubleshooting.</summary>
public sealed class GraphRequestException : PublisherException
{
    public GraphRequestException(string message, int? statusCode, string? clientRequestId, string? requestId)
        : base(message)
    {
        StatusCode = statusCode;
        ClientRequestId = clientRequestId;
        RequestId = requestId;
    }

    public GraphRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status code of the final failing response, when available.</summary>
    public int? StatusCode { get; }

    /// <summary>The `client-request-id` header Graph echoes back, for support correlation. Never a secret.</summary>
    public string? ClientRequestId { get; }

    /// <summary>The `request-id` header Graph returns, for support correlation. Never a secret.</summary>
    public string? RequestId { get; }
}
