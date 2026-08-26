using System.Text;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Renders macOS PKG semantic inspection warnings as deterministic text for the interactive
/// confirmation prompt and non-interactive failure messages. Only bundle ids, versions and
/// manifest-declared facts appear - never source URLs, tokens or signed URLs.
/// </summary>
public static class PkgInspectionWarningFormatter
{
    /// <summary>
    /// Formats every warning for one app entry, grouped under its identity so a batch confirmation can
    /// show all entries' warnings before asking a single <c>[y/N]</c> question.
    /// </summary>
    public static string Format(string entryLabel, IReadOnlyList<PkgInspectionWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{entryLabel}: {warnings.Count} PKG inspection warning(s)");
        foreach (var warning in warnings)
        {
            builder.AppendLine($"  - [{DescribeCode(warning.Code)}]{DescribeBundle(warning.BundleId)} {warning.Detail}");
        }

        return builder.ToString();
    }

    /// <summary>Formats every entry's warnings into one block, in the given order.</summary>
    public static string FormatBatch(IReadOnlyList<(string EntryLabel, IReadOnlyList<PkgInspectionWarning> Warnings)> entries)
    {
        var builder = new StringBuilder();
        foreach (var (entryLabel, warnings) in entries)
        {
            builder.Append(Format(entryLabel, warnings));
        }

        return builder.ToString();
    }

    private static string DescribeBundle(string? bundleId)
        => string.IsNullOrWhiteSpace(bundleId) ? string.Empty : $" {bundleId}:";

    private static string DescribeCode(PkgInspectionWarningCode code)
        => code switch
        {
            PkgInspectionWarningCode.MultipleBundlesWithoutExplicitPrimary => "multiple-bundles-without-primary",
            PkgInspectionWarningCode.ManifestBundleNotFound => "manifest-bundle-not-found",
            PkgInspectionWarningCode.PackageBundleNotDeclared => "package-bundle-not-declared",
            PkgInspectionWarningCode.ManifestBundleVersionMismatch => "version-mismatch",
            PkgInspectionWarningCode.NoBundlesDetected => "no-bundles-detected",
            _ => code.ToString(),
        };
}
