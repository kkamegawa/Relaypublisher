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

    public List<AssignmentManifest> Assignments { get; set; } = [];
}
