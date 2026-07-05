namespace IntuneLobPublisher.Core.Manifests;

/// <summary>Install/uninstall behavior of a Windows Win32 app.</summary>
public sealed class InstallManifest
{
    public string? CommandLine { get; set; }

    public string? UninstallCommandLine { get; set; }

    /// <summary>"system" or "user".</summary>
    public string? InstallExperience { get; set; }

    /// <summary>"suppress", "allow" or "force".</summary>
    public string? RestartBehavior { get; set; }

    /// <summary>
    /// When null the Intune default set applies at publish time
    /// (0/1707 success, 3010 softReboot, 1641 hardReboot, 1618 retry).
    /// </summary>
    public List<ReturnCodeManifest>? ReturnCodes { get; set; }
}

/// <summary>Installer return code mapping.</summary>
public sealed class ReturnCodeManifest
{
    public int Code { get; set; }

    /// <summary>"success", "softReboot", "hardReboot", "retry" or "failed".</summary>
    public string? Type { get; set; }
}
