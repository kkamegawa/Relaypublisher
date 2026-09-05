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
    public static readonly IReadOnlyList<string> Architectures = ["x64", "arm64"];
    public static readonly IReadOnlyList<string> WindowsInstallerTypes = ["win32"];
    public static readonly IReadOnlyList<string> MacOsInstallerTypes = ["pkg"];

    /// <summary>"pkg" (default, macOSPkgApp) or "lob" (macOSLobApp). doc/01-manifest-schema.md §5.4.</summary>
    public static readonly IReadOnlyList<string> MacOsAppTypes = ["pkg", "lob"];

    public const string DefaultMacOsAppType = "pkg";
    public static readonly IReadOnlyList<string> InstallExperiences = ["system", "user"];
    public static readonly IReadOnlyList<string> RestartBehaviors = ["suppress", "allow", "force"];
    public static readonly IReadOnlyList<string> DetectionTypes = ["script", "file"];
    public static readonly IReadOnlyList<string> FileSystemOperationTypes = ["exists", "version"];
    public static readonly IReadOnlyList<string> FileSystemOperators =
        ["equal", "notEqual", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual"];
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

    [GeneratedRegex(@"^\d{1,5}(\.\d{1,5}){0,3}$")]
    private static partial Regex FileSystemVersionRegex();

    [GeneratedRegex(@"^[A-Za-z]:\\|^\\[^\\]|^\\\\[^\\]+\\[^\\]+(?:\\.*)?$|^%[A-Za-z_][A-Za-z0-9_()]*%\\")]
    private static partial Regex TargetDevicePathRootRegex();

    public static bool IsValidSha256(string value) => Sha256Regex().IsMatch(value);

    public static bool IsValidFileSystemVersion(string value) => FileSystemVersionRegex().IsMatch(value);

    /// <summary>
    /// Checks a Graph file-system rule path without treating it as a repository path. The value is
    /// evaluated on the target Windows device, so rooted and environment-variable paths are valid.
    /// </summary>
    public static bool IsValidTargetDevicePath(string value)
        => !HasInvalidFileSystemText(value)
            && TargetDevicePathRootRegex().IsMatch(value)
            && HasValidTargetDevicePathColonPlacement(value)
            && !HasTraversalSegment(value);

    /// <summary>Checks that a Graph file-system rule name is exactly one target-device leaf name.</summary>
    public static bool IsValidTargetDeviceLeafName(string value)
        => !HasInvalidFileSystemText(value)
            && !value.Contains('\\')
            && !value.Contains('/')
            && !string.Equals(value, ".", StringComparison.Ordinal)
            && !string.Equals(value, "..", StringComparison.Ordinal)
            && !value.Contains(':');

    /// <summary>Returns true when the SchemaVersion string has a parsable, supported major version.</summary>
    public static bool HasSupportedSchemaMajor(string schemaVersion)
    {
        var dotIndex = schemaVersion.IndexOf('.');
        var majorText = dotIndex < 0 ? schemaVersion : schemaVersion[..dotIndex];
        return int.TryParse(majorText, out var major) && major == SupportedSchemaMajor;
    }

    private static bool HasInvalidFileSystemText(string value)
        => string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.IndexOfAny(['*', '?', '<', '>', '"', '|', '/']) >= 0
            || value.Any(char.IsControl);

    private static bool HasTraversalSegment(string value)
        => value.Split('\\', StringSplitOptions.None)
            .Any(segment => string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal));

    private static bool HasValidTargetDevicePathColonPlacement(string value)
    {
        var colonIndex = value.IndexOf(':');
        return colonIndex < 0 || (colonIndex == 1 && value.Count(c => c == ':') == 1);
    }
}
