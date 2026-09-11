using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Maps a manifest `Requirements.MinimumOSVersion` (e.g. "14.0", "13") to the Graph
/// <c>macOSMinimumOperatingSystem</c> complex type, where exactly one version flag is true
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-macosminimumoperatingsystem).
/// Both <c>macOSPkgApp</c> and <c>macOSLobApp</c> are Graph beta resources
/// (doc/adr/publishing.md 2026-09-10 entry), so every version this table knows about - including the
/// <c>v14_0</c>/<c>v15_0</c>/<c>v26_0</c> flags, which have no v1.0 equivalent - is available to both.
/// </summary>
public static class MacOsMinimumOperatingSystemTable
{
    private static readonly Dictionary<string, Func<MacOsMinimumOperatingSystemPayload>> VersionToPayload =
        new(StringComparer.Ordinal)
        {
            ["10.13"] = () => new MacOsMinimumOperatingSystemPayload { V10_13 = true },
            ["10.14"] = () => new MacOsMinimumOperatingSystemPayload { V10_14 = true },
            ["10.15"] = () => new MacOsMinimumOperatingSystemPayload { V10_15 = true },
            ["11"] = () => new MacOsMinimumOperatingSystemPayload { V11_0 = true },
            ["11.0"] = () => new MacOsMinimumOperatingSystemPayload { V11_0 = true },
            ["12"] = () => new MacOsMinimumOperatingSystemPayload { V12_0 = true },
            ["12.0"] = () => new MacOsMinimumOperatingSystemPayload { V12_0 = true },
            ["13"] = () => new MacOsMinimumOperatingSystemPayload { V13_0 = true },
            ["13.0"] = () => new MacOsMinimumOperatingSystemPayload { V13_0 = true },
            ["14"] = () => new MacOsMinimumOperatingSystemPayload { V14_0 = true },
            ["14.0"] = () => new MacOsMinimumOperatingSystemPayload { V14_0 = true },
            ["15"] = () => new MacOsMinimumOperatingSystemPayload { V15_0 = true },
            ["15.0"] = () => new MacOsMinimumOperatingSystemPayload { V15_0 = true },
            ["26"] = () => new MacOsMinimumOperatingSystemPayload { V26_0 = true },
            ["26.0"] = () => new MacOsMinimumOperatingSystemPayload { V26_0 = true },
        };

    /// <exception cref="UnsupportedMacOsVersionException">The version has no known mapping.</exception>
    public static MacOsMinimumOperatingSystemPayload Map(string minimumOsVersion)
        => VersionToPayload.TryGetValue(minimumOsVersion.Trim(), out var build)
            ? build()
            : throw new UnsupportedMacOsVersionException(minimumOsVersion);
}
