using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Maps a manifest `Requirements.MinimumOSVersion` build number to the Graph `win32LobApp`
/// `minimumSupportedWindowsRelease` string (doc/01-manifest-schema.md 5.7). Graph does not type this
/// property as an enum - it is a free-form string - so the table is owned entirely by this tool.
/// </summary>
public static class WindowsReleaseTable
{
    private static readonly Dictionary<string, string> BuildToRelease = new(StringComparer.Ordinal)
    {
        ["10.0.10240"] = "Windows10_1507",
        ["10.0.10586"] = "Windows10_1511",
        ["10.0.14393"] = "Windows10_1607",
        ["10.0.15063"] = "Windows10_1703",
        ["10.0.16299"] = "Windows10_1709",
        ["10.0.17134"] = "Windows10_1803",
        ["10.0.17763"] = "Windows10_1809",
        ["10.0.18362"] = "Windows10_1903",
        ["10.0.18363"] = "Windows10_1909",
        ["10.0.19041"] = "Windows10_2004",
        ["10.0.19042"] = "Windows10_20H2",
        ["10.0.19043"] = "Windows10_21H1",
        ["10.0.19044"] = "Windows10_21H2",
        ["10.0.19045"] = "Windows10_22H2",
        ["10.0.22000"] = "Windows11_21H2",
        ["10.0.22621"] = "Windows11_22H2",
        ["10.0.22631"] = "Windows11_23H2",
        ["10.0.26100"] = "Windows11_24H2",
    };

    /// <exception cref="UnsupportedWindowsBuildException">The build number has no known mapping.</exception>
    public static string Map(string minimumOsVersion)
        => BuildToRelease.TryGetValue(minimumOsVersion.Trim(), out var release)
            ? release
            : throw new UnsupportedWindowsBuildException(minimumOsVersion);
}
