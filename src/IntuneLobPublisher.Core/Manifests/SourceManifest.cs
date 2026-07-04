namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Unified source item shape shared by Windows ExternalFiles entries and the macOS Source
/// (see doc/01-manifest-schema.md 5.0.1). Only the fields matching <see cref="Type"/> are used:
/// publicHttp -> Url / githubRelease -> Owner, Repository, Tag, AssetName / azureBlob -> AccountName, Container, BlobName.
/// </summary>
public sealed class SourceManifest
{
    /// <summary>"publicHttp", "githubRelease" or "azureBlob".</summary>
    public string? Type { get; set; }

    /// <summary>Path relative to the staging directory root.</summary>
    public string? Destination { get; set; }

    /// <summary>Expected SHA256 of the downloaded file. Required for every source type.</summary>
    public string? Sha256 { get; set; }

    public AuthManifest? Auth { get; set; }

    // publicHttp
    public string? Url { get; set; }

    // githubRelease
    public string? Owner { get; set; }
    public string? Repository { get; set; }
    public string? Tag { get; set; }
    public string? AssetName { get; set; }

    // azureBlob
    public string? AccountName { get; set; }
    public string? Container { get; set; }
    public string? BlobName { get; set; }
}

/// <summary>Authentication settings of a source item.</summary>
public sealed class AuthManifest
{
    /// <summary>"none" (default), "token" or "workloadIdentity".</summary>
    public string? Type { get; set; }

    /// <summary>Environment variable name holding the token. Required for Type: token.</summary>
    public string? SecretName { get; set; }
}
