using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Write model for the Microsoft Graph <c>win32LobApp</c> resource, used for create/update requests.
/// Built by <see cref="Win32LobAppPayloadMapper"/>. Field shapes and `@odata.type` casing follow the
/// Microsoft Learn `win32LobApp` v1.0 documentation exactly (some nested types use a leading `#`, others don't).
/// </summary>
public sealed class Win32LobAppPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobApp";

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("owner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Owner { get; init; }

    [JsonPropertyName("developer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Developer { get; init; }

    [JsonPropertyName("informationUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InformationUrl { get; init; }

    [JsonPropertyName("largeIcon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MimeContentPayload? LargeIcon { get; init; }

    /// <summary>Null when the manifest specifies no scope tags, so update requests never clear existing tags by omission.</summary>
    [JsonPropertyName("roleScopeTagIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RoleScopeTagIds { get; init; }

    [JsonPropertyName("installCommandLine")]
    public required string InstallCommandLine { get; init; }

    [JsonPropertyName("uninstallCommandLine")]
    public required string UninstallCommandLine { get; init; }

    /// <summary>
    /// Always "none" when <see cref="AllowedArchitectures"/> is set, per the Graph documentation:
    /// "When a non-null value is provided for the allowedArchitectures property, the value of the
    /// applicableArchitectures property is set to none."
    /// </summary>
    [JsonPropertyName("applicableArchitectures")]
    public string ApplicableArchitectures { get; init; } = "none";

    /// <summary>"x64" or "arm64". The v1.0 `applicableArchitectures` flags enum has no `arm64` value, so
    /// Arm64 apps must be expressed here instead (doc/00-overview.md 6.x).</summary>
    [JsonPropertyName("allowedArchitectures")]
    public required string AllowedArchitectures { get; init; }

    [JsonPropertyName("minimumSupportedWindowsRelease")]
    public required string MinimumSupportedWindowsRelease { get; init; }

    [JsonPropertyName("installExperience")]
    public required Win32LobAppInstallExperiencePayload InstallExperience { get; init; }

    /// <summary>Never empty: the Intune default set is applied when the manifest omits `Install.ReturnCodes`.</summary>
    [JsonPropertyName("returnCodes")]
    public required List<Win32LobAppReturnCodePayload> ReturnCodes { get; init; }

    /// <summary>Detection (and, in future, requirement) rules. Only PowerShell script detection is modeled today.</summary>
    [JsonPropertyName("rules")]
    public required List<Win32LobAppDetectionRulePayload> Rules { get; init; }

    [JsonPropertyName("displayVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayVersion { get; init; }

    /// <summary>
    /// Management metadata JSON for the app's <c>notes</c> field. Set on create so a brand-new app
    /// never exists without its metadata (a crash before the content upload's notes PATCH would
    /// otherwise leave an app only recoverable through the DisplayName-adopt path). Null on update,
    /// where the content upload flow refreshes notes and omission must not clear the field.
    /// </summary>
    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

public sealed class Win32LobAppInstallExperiencePayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppInstallExperience";

    /// <summary>"system" or "user".</summary>
    [JsonPropertyName("runAsAccount")]
    public required string RunAsAccount { get; init; }

    /// <summary>"suppress", "allow" or "force".</summary>
    [JsonPropertyName("deviceRestartBehavior")]
    public required string DeviceRestartBehavior { get; init; }
}

public sealed class Win32LobAppReturnCodePayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppReturnCode";

    [JsonPropertyName("returnCode")]
    public required int ReturnCode { get; init; }

    /// <summary>"success", "softReboot", "hardReboot", "retry" or "failed".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }
}

/// <summary>A Win32 detection rule. Only the PowerShell script rule shape is modeled (the only
/// detection type the manifest schema supports today).</summary>
public sealed class Win32LobAppDetectionRulePayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.win32LobAppPowerShellScriptRule";

    [JsonPropertyName("ruleType")]
    public string RuleType { get; init; } = "detection";

    [JsonPropertyName("enforceSignatureCheck")]
    public required bool EnforceSignatureCheck { get; init; }

    [JsonPropertyName("runAs32Bit")]
    public required bool RunAs32Bit { get; init; }

    /// <summary>Base64-encoded script content. The script is embedded in the Graph payload, not distributed with the package.</summary>
    [JsonPropertyName("scriptContent")]
    public required string ScriptContent { get; init; }
}

/// <summary>Used for `largeIcon`. Note the Graph JSON example omits the leading `#` on this nested type's `@odata.type`.</summary>
public sealed class MimeContentPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "microsoft.graph.mimeContent";

    /// <summary>MIME type, e.g. "image/png".</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Base64-encoded icon bytes.</summary>
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}
