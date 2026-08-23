using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Shared fields of the Graph <c>macOSPkgApp</c> and <c>macOSLobApp</c> write models, built by
/// <see cref="MacOsAppPayloadMapper"/>. Field shapes and `@odata.type` casing follow the Microsoft
/// Learn documentation exactly, mirroring <see cref="Win32LobAppPayload"/>'s conventions.
/// </summary>
public abstract class MacOsAppPayloadBase
{
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

    /// <summary>The staged .pkg's file name (manifest <c>Source.Destination</c>).</summary>
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("minimumSupportedOperatingSystem")]
    public required MacOsMinimumOperatingSystemPayload MinimumSupportedOperatingSystem { get; init; }

    /// <summary>From manifest <c>Detection.IgnoreAppVersion</c>. Defaults to false.</summary>
    [JsonPropertyName("ignoreVersionDetection")]
    public bool IgnoreVersionDetection { get; init; }

    /// <summary>
    /// Management metadata JSON for the app's <c>notes</c> field. Set on create so a brand-new app
    /// never exists without its metadata; null on update, where the content upload flow refreshes
    /// notes and omission must not clear the field (mirrors <see cref="Win32LobAppPayload.Notes"/>).
    /// </summary>
    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; init; }
}

/// <summary>
/// Write model for the Graph <c>macOSPkgApp</c> resource (unmanaged PKG, the manifest default,
/// doc/01-manifest-schema.md §5.4). Beta-only: https://learn.microsoft.com/graph/api/resources/intune-apps-macospkgapp.
/// </summary>
public sealed class MacOsPkgAppPayload : MacOsAppPayloadBase
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.macOSPkgApp";

    /// <summary>The primary included app's bundleId (CFBundleIdentifier), from the first <c>Detection.IncludedApps</c> entry.</summary>
    [JsonPropertyName("primaryBundleId")]
    public required string PrimaryBundleId { get; init; }

    /// <summary>The primary included app's version (CFBundleShortVersion), from the first <c>Detection.IncludedApps</c> entry.</summary>
    [JsonPropertyName("primaryBundleVersion")]
    public required string PrimaryBundleVersion { get; init; }

    [JsonPropertyName("includedApps")]
    public required List<MacOsIncludedAppPayload> IncludedApps { get; init; }

    /// <summary>
    /// From manifest <c>Scripts.PreInstall</c>. Only <c>macOSPkgApp</c> has this property - Graph's
    /// <c>macOSLobApp</c> / <c>macOSDmgApp</c> do not (doc/00-overview.md §6.13), so it lives here
    /// rather than on <see cref="MacOsAppPayloadBase"/>.
    /// </summary>
    [JsonPropertyName("preInstallScript")]
    // Null is intentional: an update must be able to clear a script removed from the manifest.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public MacOsAppScriptPayload? PreInstallScript { get; init; }

    /// <summary>From manifest <c>Scripts.PostInstall</c>. See <see cref="PreInstallScript"/> for why this is pkg-only.</summary>
    [JsonPropertyName("postInstallScript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public MacOsAppScriptPayload? PostInstallScript { get; init; }
}

/// <summary>
/// Write model for the Graph <c>macOSAppScript</c> complex type: a single base64-encoded shell
/// script (doc/01-manifest-schema.md §5.4.2). https://learn.microsoft.com/graph/api/resources/intune-apps-macosappscript.
/// </summary>
public sealed class MacOsAppScriptPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "microsoft.graph.macOSAppScript";

    /// <summary>Base64-encoded shell script text, CRLF-normalized to LF before encoding.</summary>
    [JsonPropertyName("scriptContent")]
    public required string ScriptContent { get; init; }
}

/// <summary>
/// Write model for the Graph <c>macOSLobApp</c> resource (managed PKG, <c>AppType: lob</c>,
/// doc/01-manifest-schema.md §5.4). Available in v1.0: https://learn.microsoft.com/graph/api/resources/intune-apps-macoslobapp.
/// </summary>
public sealed class MacOsLobAppPayload : MacOsAppPayloadBase
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.macOSLobApp";

    /// <summary>
    /// The manifest has a single per-app version value (<c>IncludedApps[0].BundleVersion</c>), not
    /// separate build/version numbers, so both <see cref="BuildNumber"/> and <see cref="VersionNumber"/>
    /// are set from it.
    /// </summary>
    [JsonPropertyName("buildNumber")]
    public required string BuildNumber { get; init; }

    /// <summary>See <see cref="BuildNumber"/> - set from the same <c>IncludedApps[0].BundleVersion</c> value.</summary>
    [JsonPropertyName("versionNumber")]
    public required string VersionNumber { get; init; }

    [JsonPropertyName("childApps")]
    public required List<MacOsLobChildAppPayload> ChildApps { get; init; }
}

/// <summary>One <c>macOSPkgApp.includedApps</c> entry (bundleId + bundleVersion).</summary>
public sealed class MacOsIncludedAppPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.macOSIncludedApp";

    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("bundleVersion")]
    public required string BundleVersion { get; init; }
}

/// <summary>One <c>macOSLobApp.childApps</c> entry (bundleId + buildNumber + versionNumber) - a different shape than <see cref="MacOsIncludedAppPayload"/>.</summary>
public sealed class MacOsLobChildAppPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.macOSLobChildApp";

    [JsonPropertyName("bundleId")]
    public required string BundleId { get; init; }

    [JsonPropertyName("buildNumber")]
    public required string BuildNumber { get; init; }

    [JsonPropertyName("versionNumber")]
    public required string VersionNumber { get; init; }
}

/// <summary>
/// The Graph <c>macOSMinimumOperatingSystem</c> complex type: exactly one version flag is true.
/// Built by <see cref="MacOsMinimumOperatingSystemTable"/>, which owns the version-to-flag mapping.
/// </summary>
public sealed class MacOsMinimumOperatingSystemPayload
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.macOSMinimumOperatingSystem";

    [JsonPropertyName("v10_13")]
    public bool V10_13 { get; init; }

    [JsonPropertyName("v10_14")]
    public bool V10_14 { get; init; }

    [JsonPropertyName("v10_15")]
    public bool V10_15 { get; init; }

    [JsonPropertyName("v11_0")]
    public bool V11_0 { get; init; }

    [JsonPropertyName("v12_0")]
    public bool V12_0 { get; init; }

    [JsonPropertyName("v13_0")]
    public bool V13_0 { get; init; }

    /// <summary>
    /// Beta-only flag (<c>AppType: pkg</c> only; see <see cref="MacOsMinimumOperatingSystemTable"/>).
    /// Nullable and omitted when unset (unlike the v1.0-only flags above): Graph's v1.0
    /// <c>macOSMinimumOperatingSystem</c> has no <c>v14_0</c> property at all, so sending it as a plain
    /// <c>false</c> on a <c>macOSLobApp</c> (v1.0) request makes Graph reject the whole call with 400
    /// "The property 'v14_0' does not exist on type 'microsoft.graph.macOSMinimumOperatingSystem'".
    /// </summary>
    [JsonPropertyName("v14_0")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? V14_0 { get; init; }

    /// <summary>Beta-only flag; see <see cref="V14_0"/> for why this is nullable.</summary>
    [JsonPropertyName("v15_0")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? V15_0 { get; init; }

    /// <summary>Beta-only flag; see <see cref="V14_0"/> for why this is nullable.</summary>
    [JsonPropertyName("v26_0")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? V26_0 { get; init; }
}
