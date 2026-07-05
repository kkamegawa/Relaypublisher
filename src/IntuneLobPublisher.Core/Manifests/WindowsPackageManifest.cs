namespace IntuneLobPublisher.Core.Manifests;

/// <summary>Windows Win32 package composition: setup file, repository files and external binaries.</summary>
public sealed class WindowsPackageManifest
{
    public IntuneWinManifest? IntuneWin { get; set; }

    public List<RepositoryFileManifest> RepositoryFiles { get; set; } = [];

    /// <summary>External binaries fetched from a source provider (unified source item shape).</summary>
    public List<SourceManifest> ExternalFiles { get; set; } = [];
}

/// <summary>Settings for .intunewin generation.</summary>
public sealed class IntuneWinManifest
{
    /// <summary>Setup file path relative to the staging directory root.</summary>
    public string? SetupFile { get; set; }
}

/// <summary>A file copied from the repository into the staging directory.</summary>
public sealed class RepositoryFileManifest
{
    /// <summary>Path relative to the repository root.</summary>
    public string? Source { get; set; }

    /// <summary>Path relative to the staging directory root.</summary>
    public string? Destination { get; set; }
}
