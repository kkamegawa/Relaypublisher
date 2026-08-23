using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Maps a manifest `Requirements.MinimumOSVersion` (e.g. "14.0", "13") to the Graph
/// <c>macOSMinimumOperatingSystem</c> complex type, where exactly one version flag is true
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-macosminimumoperatingsystem).
/// v1.0 only defines flags through <c>v13_0</c>; <c>v14_0</c>/<c>v15_0</c>/<c>v26_0</c> are beta-only,
/// so a <c>macOSLobApp</c> (v1.0, <see cref="Publishing.MacOsLobAppPayload"/>) cannot target macOS 14+.
/// </summary>
public static class MacOsMinimumOperatingSystemTable
{
    private static readonly Dictionary<string, (bool IsBetaOnly, Func<MacOsMinimumOperatingSystemPayload> Build)> VersionToPayload =
        new(StringComparer.Ordinal)
        {
            ["10.13"] = (false, () => new MacOsMinimumOperatingSystemPayload { V10_13 = true }),
            ["10.14"] = (false, () => new MacOsMinimumOperatingSystemPayload { V10_14 = true }),
            ["10.15"] = (false, () => new MacOsMinimumOperatingSystemPayload { V10_15 = true }),
            ["11"] = (false, () => new MacOsMinimumOperatingSystemPayload { V11_0 = true }),
            ["11.0"] = (false, () => new MacOsMinimumOperatingSystemPayload { V11_0 = true }),
            ["12"] = (false, () => new MacOsMinimumOperatingSystemPayload { V12_0 = true }),
            ["12.0"] = (false, () => new MacOsMinimumOperatingSystemPayload { V12_0 = true }),
            ["13"] = (false, () => new MacOsMinimumOperatingSystemPayload { V13_0 = true }),
            ["13.0"] = (false, () => new MacOsMinimumOperatingSystemPayload { V13_0 = true }),
            ["14"] = (true, () => new MacOsMinimumOperatingSystemPayload { V14_0 = true }),
            ["14.0"] = (true, () => new MacOsMinimumOperatingSystemPayload { V14_0 = true }),
            ["15"] = (true, () => new MacOsMinimumOperatingSystemPayload { V15_0 = true }),
            ["15.0"] = (true, () => new MacOsMinimumOperatingSystemPayload { V15_0 = true }),
            ["26"] = (true, () => new MacOsMinimumOperatingSystemPayload { V26_0 = true }),
            ["26.0"] = (true, () => new MacOsMinimumOperatingSystemPayload { V26_0 = true }),
        };

    /// <param name="useBeta">Whether the app being mapped is beta-only (<c>AppType: pkg</c>). A v1.0-only
    /// app (<c>AppType: lob</c>) fails for a beta-only version instead of silently going through.</param>
    /// <exception cref="UnsupportedMacOsVersionException">
    /// The version has no known mapping, or is beta-only (macOS 14+) and <paramref name="useBeta"/> is false.
    /// </exception>
    public static MacOsMinimumOperatingSystemPayload Map(string minimumOsVersion, bool useBeta)
    {
        if (!VersionToPayload.TryGetValue(minimumOsVersion.Trim(), out var entry))
        {
            throw new UnsupportedMacOsVersionException(minimumOsVersion);
        }

        if (entry.IsBetaOnly && !useBeta)
        {
            throw new UnsupportedMacOsVersionException(minimumOsVersion, requiresBetaOnlyFlag: true);
        }

        return entry.Build();
    }
}
