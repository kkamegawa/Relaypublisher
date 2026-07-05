namespace IntuneLobPublisher.Core.Manifests;

/// <summary>One assignment entry (see doc/01-manifest-schema.md 5.5).</summary>
public sealed class AssignmentManifest
{
    /// <summary>"group" (default), "allDevices" or "allLicensedUsers".</summary>
    public string? Target { get; set; }

    /// <summary>Entra ID group GUID. Required for Target: group, forbidden otherwise.</summary>
    public string? GroupId { get; set; }

    /// <summary>"include" (default) or "exclude". Intent only applies to include targets.</summary>
    public string? Mode { get; set; }

    /// <summary>"required", "available" or "uninstall".</summary>
    public string? Intent { get; set; }

    /// <summary>Assignment filter GUID (optional).</summary>
    public string? FilterId { get; set; }

    /// <summary>"include" or "exclude". Required when FilterId is set.</summary>
    public string? FilterMode { get; set; }

    /// <summary>Win32 only optional settings.</summary>
    public AssignmentSettingsManifest? Settings { get; set; }
}

/// <summary>Optional Win32 assignment settings.</summary>
public sealed class AssignmentSettingsManifest
{
    /// <summary>"showAll", "showReboot" or "hideAll".</summary>
    public string? Notifications { get; set; }

    public int? RestartGracePeriodMinutes { get; set; }
}
