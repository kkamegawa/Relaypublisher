using System.Text.RegularExpressions;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>Allowed manifest field values. Matching is case-sensitive by design.</summary>
public static partial class ManifestValues
{
    /// <summary>Only manifests with this SchemaVersion major are accepted.</summary>
    public const int SupportedSchemaMajor = 1;

    public const string DefaultAssignmentSync = "merge";
    public const string DefaultAssignmentTarget = "group";
    public const string DefaultAssignmentMode = "include";

    public static readonly IReadOnlyList<string> Platforms = ["windows", "macos"];

    /// <summary>Windows requires Architecture; it maps to the Graph allowedArchitectures value.</summary>
    public static readonly IReadOnlyList<string> WindowsArchitectures = ["x64", "arm64"];

    /// <summary>
    /// macOS app resources have no Graph architecture property, so Architecture is optional there;
    /// "universal" is only meaningful as the effective value of an omitted Architecture
    /// (see <see cref="Manifests.AppArchitecture"/>) but is also accepted when declared explicitly.
    /// </summary>
    public static readonly IReadOnlyList<string> MacOsArchitectures = ["x64", "arm64", "universal"];

    public static readonly IReadOnlyList<string> WindowsInstallerTypes = ["win32"];
    public static readonly IReadOnlyList<string> MacOsInstallerTypes = ["pkg"];

    /// <summary>"pkg" (default, macOSPkgApp) or "lob" (macOSLobApp). doc/01-manifest-schema.md §5.4.</summary>
    public static readonly IReadOnlyList<string> MacOsAppTypes = ["pkg", "lob"];

    public const string DefaultMacOsAppType = "pkg";
    public static readonly IReadOnlyList<string> InstallExperiences = ["system", "user"];
    public static readonly IReadOnlyList<string> RestartBehaviors = ["suppress", "allow", "force"];
    public static readonly IReadOnlyList<string> DetectionTypes = ["script"];
    public static readonly IReadOnlyList<string> AssignmentTargets = ["group", "allDevices", "allLicensedUsers"];
    public static readonly IReadOnlyList<string> AssignmentModes = ["include", "exclude"];
    public static readonly IReadOnlyList<string> AssignmentIntents = ["required", "available", "uninstall"];
    public static readonly IReadOnlyList<string> FilterModes = ["include", "exclude"];
    public static readonly IReadOnlyList<string> SourceTypes = ["publicHttp", "githubRelease", "azureBlob"];
    public static readonly IReadOnlyList<string> AuthTypes = ["none", "token", "workloadIdentity"];
    public static readonly IReadOnlyList<string> ReturnCodeTypes = ["success", "softReboot", "hardReboot", "retry", "failed"];
    public static readonly IReadOnlyList<string> AssignmentSyncModes = ["merge", "replace"];
    public static readonly IReadOnlyList<string> NotificationValues = ["showAll", "showReboot", "hideAll"];

    /// <summary>Icon file extensions Graph's largeIcon MIME mapping recognizes (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> IconExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Operational upper bound on Icon file size, so oversized icons fail before a Graph call.</summary>
    public const long MaxIconBytes = 1 * 1024 * 1024;

    /// <summary>File extension required for macOS pre/post-install scripts (doc/01-manifest-schema.md §5.4.2).</summary>
    public const string MacOsScriptExtension = ".sh";

    /// <summary>
    /// Graph's documented limit for <c>macOSAppScript.scriptContent</c> (the un-encoded script text,
    /// not its base64 form): https://learn.microsoft.com/intune/app-management/deployment/add-unmanaged-pkg-macos.
    /// </summary>
    public const int MaxMacOsAppScriptChars = 15360;

    /// <summary>
    /// Upper bound on a pre/post-install script file's raw byte size, checked before reading it into
    /// memory. A valid UTF-8 character uses at most four bytes, and CRLF normalization can only reduce
    /// the character count, so this bound safely rejects an oversized file before allocating an
    /// unbounded buffer. Shared by <c>ManifestAssetValidator</c> (validate-time) and
    /// <c>ManifestAssetReader</c> (publish-time, the last local gate before the Graph call).
    /// </summary>
    public const long MaxMacOsAppScriptBytes = (long)MaxMacOsAppScriptChars * 4 + 3;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    public static bool IsValidSha256(string value) => Sha256Regex().IsMatch(value);

    /// <summary>Returns true when the SchemaVersion string has a parsable, supported major version.</summary>
    public static bool HasSupportedSchemaMajor(string schemaVersion)
    {
        var dotIndex = schemaVersion.IndexOf('.');
        var majorText = dotIndex < 0 ? schemaVersion : schemaVersion[..dotIndex];
        return int.TryParse(majorText, out var major) && major == SupportedSchemaMajor;
    }
}
