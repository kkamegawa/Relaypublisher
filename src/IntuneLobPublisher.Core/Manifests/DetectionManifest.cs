namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// App detection settings. Windows uses script or file-system detection.
/// macOS has no script detection; it is always detected via <see cref="IncludedApps"/>
/// (doc/01-manifest-schema.md §5.3, Graph <c>includedApps</c> / <c>childApps</c>).
/// </summary>
public sealed class DetectionManifest
{
    /// <summary>Windows only: "script" or "file".</summary>
    public string? Type { get; set; }

    /// <summary>Windows only: detection script path relative to the repository root.</summary>
    public string? ScriptFile { get; set; }

    public bool? RunAs32Bit { get; set; }

    public bool? EnforceSignatureCheck { get; set; }

    /// <summary>Windows <c>Type: file</c> only: target-device directory path.</summary>
    public string? Path { get; set; }

    /// <summary>Windows <c>Type: file</c> only: target-device file or folder leaf name.</summary>
    public string? FileOrFolderName { get; set; }

    /// <summary>Windows <c>Type: file</c> only: "exists" or "version".</summary>
    public string? OperationType { get; set; }

    /// <summary>Windows <c>Type: file</c> only: the version comparison operator.</summary>
    public string? Operator { get; set; }

    /// <summary>Windows <c>Type: file</c> only: the version comparison value.</summary>
    public string? ComparisonValue { get; set; }

    /// <summary>Windows <c>Type: file</c> only: expands environment variables in 32-bit context.</summary>
    public bool? Check32BitOn64System { get; set; }

    /// <summary>
    /// macOS only: bundleId + version list Intune uses to detect the app (Graph <c>includedApps</c> /
    /// <c>childApps</c>). At least one entry is required; the first entry is also used for report display
    /// and, for <c>AppType: pkg</c>, as the app's <c>primaryBundleId</c> / <c>primaryBundleVersion</c>.
    /// </summary>
    public List<IncludedAppManifest>? IncludedApps { get; set; }

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

    /// <summary>The app's CFBundleShortVersion.</summary>
    public string? BundleVersion { get; set; }
}
