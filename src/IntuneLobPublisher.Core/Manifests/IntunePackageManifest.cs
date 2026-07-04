namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Root model of a package manifest file.
/// Field presence is enforced by validation, not by the YAML loader,
/// so all members are nullable and populated as-is from the document.
/// </summary>
public sealed class IntunePackageManifest
{
    /// <summary>Manifest schema version, e.g. "1.0". Unknown major versions are rejected.</summary>
    public string? SchemaVersion { get; set; }

    public string? PackageIdentifier { get; set; }

    public string? PackageName { get; set; }

    public string? Publisher { get; set; }

    public string? Description { get; set; }

    public string? PackageVersion { get; set; }

    public string? Owner { get; set; }

    public string? Developer { get; set; }

    public string? InformationUrl { get; set; }

    /// <summary>Repository-relative path to the icon registered as largeIcon.</summary>
    public string? Icon { get; set; }

    public List<string> RoleScopeTagIds { get; set; } = [];

    /// <summary>"merge" (default when omitted) or "replace".</summary>
    public string? AssignmentSync { get; set; }

    public List<AppManifest> Apps { get; set; } = [];
}
