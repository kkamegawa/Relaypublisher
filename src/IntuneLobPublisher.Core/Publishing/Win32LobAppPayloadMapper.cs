using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

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
    /// <param name="detectionScriptContent">Raw (not base64) content of `Detection.ScriptFile`, already read from the repository.</param>
    /// <param name="iconBytes">Raw icon bytes read from the repository, or null when the manifest has no `Icon` / it was not supplied.</param>
    /// <param name="notes">Management metadata JSON for the `notes` field, set only on create requests (see <see cref="Win32LobAppPayload.Notes"/>).</param>
    /// <exception cref="UnsupportedWindowsBuildException">`Requirements.MinimumOSVersion` has no known release mapping.</exception>
    /// <exception cref="UnsupportedIconFormatException">`iconBytes` was supplied but `Icon`'s extension is not recognized.</exception>
    public static Win32LobAppPayload Map(
        IntunePackageManifest manifest,
        AppManifest app,
        string detectionScriptContent,
        byte[]? iconBytes,
        string? notes = null)
    {
        var install = app.Install!;
        var detection = app.Detection;

        return new Win32LobAppPayload
        {
            DisplayName = app.DisplayName!,
            Description = manifest.Description!,
            Publisher = manifest.Publisher!,
            Owner = manifest.Owner,
            Developer = manifest.Developer,
            InformationUrl = manifest.InformationUrl,
            LargeIcon = MapLargeIcon(manifest.Icon, iconBytes),
            RoleScopeTagIds = manifest.RoleScopeTagIds is { Count: > 0 } ? manifest.RoleScopeTagIds : null,
            InstallCommandLine = install.CommandLine!,
            UninstallCommandLine = install.UninstallCommandLine!,
            AllowedArchitectures = app.Architecture!,
            MinimumSupportedWindowsRelease = WindowsReleaseTable.Map(app.Requirements!.MinimumOSVersion!),
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

    private static Win32LobAppDetectionRulePayload MapDetectionRule(DetectionManifest? detection, string detectionScriptContent)
        => new()
        {
            EnforceSignatureCheck = detection?.EnforceSignatureCheck ?? false,
            RunAs32Bit = detection?.RunAs32Bit ?? false,
            ScriptContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(detectionScriptContent)),
        };

    private static MimeContentPayload? MapLargeIcon(string? iconPath, byte[]? iconBytes)
    {
        if (iconBytes is null || iconPath is null)
        {
            return null;
        }

        return new MimeContentPayload
        {
            Type = ResolveIconMimeType(iconPath),
            Value = Convert.ToBase64String(iconBytes),
        };
    }

    private static string ResolveIconMimeType(string iconPath) => Path.GetExtension(iconPath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => throw new UnsupportedIconFormatException(iconPath),
    };
}
