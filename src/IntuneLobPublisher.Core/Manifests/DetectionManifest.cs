namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// App detection settings. Windows uses script detection (<see cref="Type"/> / <see cref="ScriptFile"/>).
/// macOS has no script detection; it is always detected via <see cref="IncludedApps"/>
/// (doc/01-manifest-schema.md §5.3, Graph <c>includedApps</c> / <c>childApps</c>).
/// </summary>
public sealed class DetectionManifest
{
    /// <summary>Windows only: "script".</summary>
    public string? Type { get; set; }

    /// <summary>Windows only: detection script path relative to the repository root.</summary>
    public string? ScriptFile { get; set; }

    public bool? RunAs32Bit { get; set; }

    public bool? EnforceSignatureCheck { get; set; }

    /// <summary>
    /// macOS only: bundleId + version list Intune uses to detect the app (Graph <c>includedApps</c> /
    /// <c>childApps</c>). At least one entry is required. The first entry is used as the primary bundle
    /// unless <see cref="PrimaryBundleId"/> selects another entry.
    /// </summary>
    public List<IncludedAppManifest>? IncludedApps { get; set; }

    /// <summary>
    /// macOS only: optional bundle identifier used to select the primary entry from
    /// <see cref="IncludedApps"/>. Matching is ordinal and case-sensitive: an exact match or a
    /// segment-boundary prefix match (the selected value followed by <c>.</c>) is required to resolve
    /// exactly one entry. When omitted, the first entry remains primary.
    /// </summary>
    public string? PrimaryBundleId { get; set; }

    /// <summary>
    /// macOS only: when true, the app's version is not used to detect whether it is installed
    /// (Graph <c>ignoreVersionDetection</c>). Defaults to false when omitted.
    /// </summary>
    public bool? IgnoreAppVersion { get; set; }
}

/// <summary>One macOS <see cref="DetectionManifest.IncludedApps"/> entry.</summary>
public sealed class IncludedAppManifest
{
    /// <summary>The app's CFBundleIdentifier.</summary>
    public string? BundleId { get; set; }

    /// <summary>The app's CFBundleShortVersionString.</summary>
    public string? BundleVersion { get; set; }

    /// <summary>The app's CFBundleVersion. Required for macOS <c>AppType: lob</c>; optional for <c>pkg</c>.</summary>
    public string? BundleBuildVersion { get; set; }
}
