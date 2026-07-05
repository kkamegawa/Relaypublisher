namespace IntuneLobPublisher.Core.Manifests;

/// <summary>App detection settings. Only script detection is supported for Windows at this stage.</summary>
public sealed class DetectionManifest
{
    /// <summary>"script".</summary>
    public string? Type { get; set; }

    /// <summary>Detection script path relative to the repository root.</summary>
    public string? ScriptFile { get; set; }

    public bool? RunAs32Bit { get; set; }

    public bool? EnforceSignatureCheck { get; set; }
}
