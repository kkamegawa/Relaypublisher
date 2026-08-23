using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Request body for <c>POST .../mobileApps/{id}/{appType}/contentVersions</c>.</summary>
public sealed class MobileAppContentCreateRequest
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.mobileAppContent";
}

/// <summary>Response body for a created <c>mobileAppContent</c> (content version).</summary>
public sealed class MobileAppContentResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>Request body for <c>POST .../mobileApps/{id}/{appType}/contentVersions/{cv}/files</c>.</summary>
public sealed class MobileAppContentFileCreateRequest
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.mobileAppContentFile";

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Original (unencrypted) size in bytes.</summary>
    [JsonPropertyName("size")]
    public required long Size { get; init; }

    /// <summary>Size in bytes after IntuneWinAppUtil's encryption.</summary>
    [JsonPropertyName("sizeEncrypted")]
    public required long SizeEncrypted { get; init; }
}

/// <summary>
/// Read model for <c>mobileAppContentFile</c>, used both for the create response and for polling GETs.
/// See https://learn.microsoft.com/graph/api/resources/intune-apps-mobileappcontentfile.
/// </summary>
public sealed class MobileAppContentFileResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("sizeEncrypted")]
    public long? SizeEncrypted { get; init; }

    [JsonPropertyName("azureStorageUri")]
    public string? AzureStorageUri { get; init; }

    [JsonPropertyName("azureStorageUriExpirationDateTime")]
    public DateTimeOffset? AzureStorageUriExpirationDateTime { get; init; }

    /// <summary>
    /// Whether Intune has fully committed this file. Nullable so recovery fails safely when Graph omits
    /// the field instead of treating an unknown state as an uncommitted file that may be reused.
    /// </summary>
    [JsonPropertyName("isCommitted")]
    public bool? IsCommitted { get; init; }

    /// <summary>
    /// A <c>mobileAppContentFileUploadState</c> member name, e.g. <c>azureStorageUriRequestSuccess</c> or
    /// <c>commitFileFailed</c>. See https://learn.microsoft.com/graph/api/resources/intune-apps-mobileappcontentfileuploadstate.
    /// </summary>
    [JsonPropertyName("uploadState")]
    public required string UploadState { get; init; }
}

/// <summary>
/// The <c>fileEncryptionInfo</c> commit parameter
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-fileencryptioninfo). Byte[] properties
/// are serialized as base64 strings by <c>System.Text.Json</c>, matching the Graph <c>Binary</c> type.
/// </summary>
public sealed class FileEncryptionInfoPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.fileEncryptionInfo";

    [JsonPropertyName("encryptionKey")]
    public required byte[] EncryptionKey { get; init; }

    /// <summary>Must be 16 bytes.</summary>
    [JsonPropertyName("initializationVector")]
    public required byte[] InitializationVector { get; init; }

    /// <summary>Must be 32 bytes.</summary>
    [JsonPropertyName("mac")]
    public required byte[] Mac { get; init; }

    /// <summary>Must be 32 bytes.</summary>
    [JsonPropertyName("macKey")]
    public required byte[] MacKey { get; init; }

    /// <summary>Only "ProfileVersion1" is currently supported by Graph.</summary>
    [JsonPropertyName("profileIdentifier")]
    public required string ProfileIdentifier { get; init; }

    [JsonPropertyName("fileDigest")]
    public required byte[] FileDigest { get; init; }

    /// <summary>ProfileVersion1 currently only supports "SHA256".</summary>
    [JsonPropertyName("fileDigestAlgorithm")]
    public required string FileDigestAlgorithm { get; init; }
}

/// <summary>Request body for <c>POST .../files/{f}/commit</c>.</summary>
public sealed class CommitFileRequest
{
    [JsonPropertyName("fileEncryptionInfo")]
    public required FileEncryptionInfoPayload FileEncryptionInfo { get; init; }
}

/// <summary>
/// PATCH body for updating a mobile LOB app after content upload. Shared by the
/// <c>committedContentVersion</c> patch (step 8) and the management-metadata <c>notes</c> patch (step 10) -
/// each call only sets the field it needs, and the other is omitted rather than sent as null.
/// <see cref="ODataType"/> has no default: callers must state the app's concrete Graph type
/// (<c>win32LobApp</c>, <c>macOSPkgApp</c>, <c>macOSLobApp</c>) explicitly.
/// </summary>
public sealed class MobileAppMetadataPatchPayload
{
    [JsonPropertyName("@odata.type")]
    public required string ODataType { get; init; }

    [JsonPropertyName("committedContentVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommittedContentVersion { get; init; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

/// <summary>Read model for a mobile LOB app's content publishing state.</summary>
public sealed class MobileLobAppContentStateResponse
{
    /// <summary>One of "notPublished", "processing", "published".</summary>
    [JsonPropertyName("publishingState")]
    public required string PublishingState { get; init; }

    [JsonPropertyName("committedContentVersion")]
    public string? CommittedContentVersion { get; init; }
}

public sealed record MobileAppContentState(string PublishingState, string? CommittedContentVersion);
