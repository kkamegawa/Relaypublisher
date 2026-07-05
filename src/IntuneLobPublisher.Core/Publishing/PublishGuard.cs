namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Outcome of comparing the manifest's version against the version stored on the existing app.</summary>
public enum VersionGuardResult
{
    /// <summary>No stored version (new app), or the manifest version is not lower: publish proceeds.</summary>
    Proceed,

    /// <summary>The manifest version is lower than the stored version and `--allow-downgrade` was not given: skip with a warning.</summary>
    SkipDowngrade,
}

/// <summary>Whether content upload can be skipped because the input hash has not changed.</summary>
public enum ContentUploadDecision
{
    Upload,
    Skip,
}

/// <summary>
/// Version and inputHash guards (doc/00-overview.md 6.7/6.8). Pure comparison logic over
/// already-resolved metadata; it performs no Graph calls itself.
/// </summary>
public static class PublishGuard
{
    /// <summary>
    /// Compares dotted numeric version segments (e.g. "1.10.2" > "1.9.0"). Segments that are not
    /// parseable as non-negative integers fall back to an ordinal string comparison of that segment,
    /// so unexpected formats degrade gracefully instead of throwing.
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        var leftSegments = left.Split('.');
        var rightSegments = right.Split('.');
        var length = Math.Max(leftSegments.Length, rightSegments.Length);

        for (var i = 0; i < length; i++)
        {
            var leftSegment = i < leftSegments.Length ? leftSegments[i] : "0";
            var rightSegment = i < rightSegments.Length ? rightSegments[i] : "0";

            int comparison;
            if (int.TryParse(leftSegment, out var leftNumber) && int.TryParse(rightSegment, out var rightNumber))
            {
                comparison = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                comparison = string.CompareOrdinal(leftSegment, rightSegment);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// Evaluates the downgrade guard. <paramref name="storedPackageVersion"/> is <see langword="null"/>
    /// for a new app (nothing to compare against, so it always proceeds).
    /// </summary>
    public static VersionGuardResult EvaluateVersion(string? storedPackageVersion, string manifestPackageVersion, bool allowDowngrade)
    {
        if (storedPackageVersion is null || allowDowngrade)
        {
            return VersionGuardResult.Proceed;
        }

        return CompareVersions(manifestPackageVersion, storedPackageVersion) < 0
            ? VersionGuardResult.SkipDowngrade
            : VersionGuardResult.Proceed;
    }

    /// <summary>
    /// Evaluates the inputHash idempotency guard. <paramref name="storedInputHash"/> is
    /// <see langword="null"/> for a new app (nothing to skip, content must always be uploaded).
    /// </summary>
    public static ContentUploadDecision EvaluateContentUpload(string? storedInputHash, string manifestInputHash)
    {
        return storedInputHash is not null && string.Equals(storedInputHash, manifestInputHash, StringComparison.Ordinal)
            ? ContentUploadDecision.Skip
            : ContentUploadDecision.Upload;
    }
}
