namespace IntuneLobPublisher.Core.Manifests;

/// <summary>Minimum requirements of the target device.</summary>
public sealed class RequirementsManifest
{
    /// <summary>Windows: build version such as "10.0.19045". macOS: version such as "14.0".</summary>
    public string? MinimumOSVersion { get; set; }

    /// <summary>
    /// Windows only: must match the app-level Architecture. Forbidden on macOS, which has no separate
    /// "requirements architecture" concept.
    /// </summary>
    public string? Architecture { get; set; }
}
