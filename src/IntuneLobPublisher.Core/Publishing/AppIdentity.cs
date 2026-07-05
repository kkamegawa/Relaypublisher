namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The app identity key (doc/00-overview.md 6.1): <c>PackageIdentifier + Platform + Architecture</c>.
/// Intune has no native package-identifier field, so this triple is matched against management
/// metadata stored in `notes` to find the app that owns a given manifest entry.
/// </summary>
public readonly record struct AppIdentity(string PackageIdentifier, string Platform, string Architecture)
{
    /// <summary>
    /// True when <paramref name="metadata"/> was written for this identity. <see cref="PackageIdentifier"/>
    /// is compared ordinally (it is a stable, case-sensitive manifest key); platform/architecture are
    /// compared ignoring case since manifests and Graph may not agree on casing.
    /// </summary>
    public bool Matches(ManagementMetadata metadata)
        => string.Equals(PackageIdentifier, metadata.PackageIdentifier, StringComparison.Ordinal)
        && string.Equals(Platform, metadata.Platform, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Architecture, metadata.Architecture, StringComparison.OrdinalIgnoreCase);
}
