namespace IntuneLobPublisher.Core.Manifests;

/// <summary>One platform/architecture specific app entry inside a manifest.</summary>
public sealed class AppManifest
{
    /// <summary>"windows" or "macos".</summary>
    public string? Platform { get; set; }

    /// <summary>"x64" or "arm64".</summary>
    public string? Architecture { get; set; }

    /// <summary>"win32" (Windows) or "pkg" (macOS).</summary>
    public string? InstallerType { get; set; }

    /// <summary>macOS only: "pkg" (default, macOSPkgApp) or "lob" (macOSLobApp).</summary>
    public string? AppType { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Windows package definition.</summary>
    public WindowsPackageManifest? Package { get; set; }

    /// <summary>macOS single source item (unified source item shape).</summary>
    public SourceManifest? Source { get; set; }

    public InstallManifest? Install { get; set; }

    public DetectionManifest? Detection { get; set; }

    public RequirementsManifest? Requirements { get; set; }

    /// <summary>macOS <c>AppType: pkg</c> only: pre/post-install shell scripts (doc/01-manifest-schema.md §5.4.2).</summary>
    public MacOsScriptsManifest? Scripts { get; set; }

    public List<AssignmentManifest> Assignments { get; set; } = [];

    /// <summary>
    /// Intune app category display names this entry should be related to (doc/01-manifest-schema.md §5.8).
    /// Deliberately nullable with no initializer so "omitted" and "empty list" stay distinguishable:
    /// null preserves the app's current category relationships and issues no category Graph call, an
    /// empty list removes every relationship, and a non-empty list is the exact desired set. The
    /// canonical JSON used by <c>InputHashCalculator</c> drops nulls, so manifests that do not declare
    /// Categories keep their existing manifestHash/inputHash.
    /// </summary>
    public List<string>? Categories { get; set; }
}
