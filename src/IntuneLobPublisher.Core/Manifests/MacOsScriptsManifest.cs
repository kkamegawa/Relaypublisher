namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Pre/post-install shell scripts for a macOS <c>AppType: pkg</c> app entry
/// (doc/00-overview.md §6.13, doc/01-manifest-schema.md §5.4.2). Both fields are
/// repository-relative paths, resolved the same way as <see cref="IntunePackageManifest.Icon"/>
/// and <see cref="DetectionManifest.ScriptFile"/>. Either field may be omitted.
/// </summary>
public sealed class MacOsScriptsManifest
{
    /// <summary>Path to the shell script Graph runs before the .pkg installs.</summary>
    public string? PreInstall { get; set; }

    /// <summary>Path to the shell script Graph runs after the .pkg installs successfully.</summary>
    public string? PostInstall { get; set; }
}
