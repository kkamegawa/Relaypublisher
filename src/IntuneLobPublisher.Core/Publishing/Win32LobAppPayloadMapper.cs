using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Maps a validated manifest entry to a Graph <see cref="Win32LobAppPayload"/> (doc/issues/issue-003-intune-graph-win32.md).
/// Pure mapping logic: no file I/O and no Graph calls. Callers read the detection script and icon bytes
/// (subject to the usual manifest path-safety checks) and pass the raw content in.
/// </summary>
public static class Win32LobAppPayloadMapper
{
    /// <summary>Applied when the manifest omits `Install.ReturnCodes`, so `returnCodes` is never empty.</summary>
    public static readonly IReadOnlyList<Win32LobAppReturnCodePayload> DefaultReturnCodes =
    [
        new() { ReturnCode = 0, Type = "success" },
        new() { ReturnCode = 1707, Type = "success" },
        new() { ReturnCode = 3010, Type = "softReboot" },
        new() { ReturnCode = 1641, Type = "hardReboot" },
        new() { ReturnCode = 1618, Type = "retry" },
    ];

    /// <param name="manifest">The root manifest, for top-level app info (description/publisher/owner/etc).</param>
    /// <param name="app">The platform/architecture-specific entry being published.</param>
    /// <param name="detectionScriptContent">Raw (not base64) content of `Detection.ScriptFile`, already read from the repository for script detection.</param>
    /// <param name="iconBytes">Raw icon bytes read from the repository, or null when the manifest has no `Icon` / it was not supplied.</param>
    /// <param name="notes">Management metadata JSON for the `notes` field, set only on create requests (see <see cref="Win32LobAppPayload.Notes"/>).</param>
    /// <exception cref="UnsupportedWindowsBuildException">`Requirements.MinimumOSVersion` has no known release mapping.</exception>
    /// <exception cref="UnsupportedIconFormatException">`iconBytes` was supplied but `Icon`'s extension is not recognized.</exception>
    public static Win32LobAppPayload Map(
        IntunePackageManifest manifest,
        AppManifest app,
        string? detectionScriptContent,
        byte[]? iconBytes,
        string? notes = null)
    {
        var install = app.Install!;
        var detection = app.Detection;
        var setupFile = app.Package!.IntuneWin!.SetupFile!;

        return new Win32LobAppPayload
        {
            DisplayName = app.DisplayName!,
            Description = manifest.Description!,
            Publisher = manifest.Publisher!,
            Owner = manifest.Owner,
            Developer = manifest.Developer,
            InformationUrl = manifest.InformationUrl,
            LargeIcon = IconPayloadMapper.Map(manifest.Icon, iconBytes),
            RoleScopeTagIds = manifest.RoleScopeTagIds is { Count: > 0 } ? manifest.RoleScopeTagIds : null,
            InstallCommandLine = install.CommandLine!,
            UninstallCommandLine = install.UninstallCommandLine!,
            AllowedArchitectures = app.Architecture!,
            MinimumSupportedWindowsRelease = WindowsReleaseTable.Map(app.Requirements!.MinimumOSVersion!),
            SetupFilePath = setupFile.Replace('/', '\\'),
            FileName = IntuneWinNaming.PackageFileNameFor(setupFile),
            InstallExperience = new Win32LobAppInstallExperiencePayload
            {
                RunAsAccount = install.InstallExperience!,
                DeviceRestartBehavior = install.RestartBehavior!,
            },
            ReturnCodes = MapReturnCodes(install.ReturnCodes),
            Rules = [MapDetectionRule(detection, detectionScriptContent)],
            DisplayVersion = manifest.PackageVersion,
            Notes = notes,
        };
    }

    private static List<Win32LobAppReturnCodePayload> MapReturnCodes(List<ReturnCodeManifest>? returnCodes)
        => returnCodes is { Count: > 0 }
            ? returnCodes.Select(rc => new Win32LobAppReturnCodePayload { ReturnCode = rc.Code, Type = rc.Type! }).ToList()
            : DefaultReturnCodes.ToList();

    private static Win32LobAppRulePayload MapDetectionRule(DetectionManifest? detection, string? detectionScriptContent)
        => detection?.Type switch
        {
            "script" when detectionScriptContent is not null => new Win32LobAppPowerShellScriptRulePayload
            {
                EnforceSignatureCheck = detection.EnforceSignatureCheck ?? false,
                RunAs32Bit = detection.RunAs32Bit ?? false,
                ScriptContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(detectionScriptContent)),
            },
            "script" => throw new InvalidOperationException("Detection.Type 'script' requires detection script content."),
            "file" => new Win32LobAppFileSystemRulePayload
            {
                Path = detection.Path ?? throw new InvalidOperationException("Detection.Type 'file' requires Detection.Path."),
                FileOrFolderName = detection.FileOrFolderName
                    ?? throw new InvalidOperationException("Detection.Type 'file' requires Detection.FileOrFolderName."),
                Check32BitOn64System = detection.Check32BitOn64System ?? false,
                OperationType = detection.OperationType
                    ?? throw new InvalidOperationException("Detection.Type 'file' requires Detection.OperationType."),
                Operator = detection.OperationType == "exists"
                    ? "notConfigured"
                    : detection.Operator ?? throw new InvalidOperationException(
                        "Detection.Type 'file' with OperationType 'version' requires Detection.Operator."),
                ComparisonValue = detection.OperationType == "exists"
                    ? null
                    : detection.ComparisonValue ?? throw new InvalidOperationException(
                        "Detection.Type 'file' with OperationType 'version' requires Detection.ComparisonValue."),
            },
            null => throw new InvalidOperationException("Windows app detection is required."),
            _ => throw new InvalidOperationException($"Detection.Type '{detection.Type}' has no Graph mapping."),
        };
}
