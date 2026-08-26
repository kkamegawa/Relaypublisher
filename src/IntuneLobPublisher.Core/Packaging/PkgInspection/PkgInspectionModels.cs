using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Reads the metadata entries of a macOS PKG without extracting its payload.
/// Implementations must perform checksum verification outside this boundary, before calling the inspector.
/// </summary>
public interface IPkgBundleInspector
{
    Task<PkgBundleInspectionResult> InspectAsync(
        Stream pkg,
        CancellationToken cancellationToken);
}

/// <summary>Facts discovered in the XAR metadata of one PKG.</summary>
public sealed record PkgBundleInspectionResult(
    string InspectorVersion,
    IReadOnlyList<PkgBundleIdentity> Bundles);

/// <summary>One application bundle declared by a PackageInfo or Distribution metadata entry.</summary>
public sealed record PkgBundleIdentity(
    string BundleId,
    string? BundleVersion,
    string? BundleBuildVersion,
    string SourceEntry);

/// <summary>Manifest-aware inspection output. The XAR inspector itself returns only archive facts.</summary>
public sealed record PkgInspectionReport(
    PkgBundleInspectionResult Inspection,
    string? SelectedPrimaryBundleId,
    IReadOnlyList<PkgInspectionWarning> Warnings,
    bool ForceAcknowledged);

/// <summary>A semantic difference between manifest declarations and a package's metadata.</summary>
public sealed record PkgInspectionWarning(
    PkgInspectionWarningCode Code,
    string? BundleId,
    string? Detail);

[JsonConverter(typeof(JsonStringEnumConverter<PkgInspectionWarningCode>))]
public enum PkgInspectionWarningCode
{
    MultipleBundlesWithoutExplicitPrimary,
    ManifestBundleNotFound,
    PackageBundleNotDeclared,
    ManifestBundleVersionMismatch,

    /// <summary>The PKG's XAR metadata declared zero application bundles.</summary>
    NoBundlesDetected,
}
