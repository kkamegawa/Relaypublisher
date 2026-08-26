namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Selects a macOS primary bundle and returns a stable selected-first projection of the declared
/// <see cref="IncludedAppManifest"/> entries. The selector deliberately does not inspect package
/// contents; package inspection and manifest reconciliation are separate concerns.
/// </summary>
public static class MacOsBundleSelector
{
    /// <summary>
    /// Finds all entries matching <paramref name="primaryBundleId"/> using ordinal, case-sensitive
    /// comparison. A match is either an exact bundle identifier or an identifier beginning with the
    /// requested value followed by a dot (a segment-boundary prefix).
    /// </summary>
    public static IReadOnlyList<int> FindMatchingIndexes(
        string primaryBundleId,
        IReadOnlyList<IncludedAppManifest> includedApps)
    {
        ArgumentNullException.ThrowIfNull(primaryBundleId);
        ArgumentNullException.ThrowIfNull(includedApps);

        var prefix = primaryBundleId + ".";
        var matches = new List<int>();
        for (var index = 0; index < includedApps.Count; index++)
        {
            var bundleId = includedApps[index].BundleId;
            if (bundleId is not null
                && (string.Equals(bundleId, primaryBundleId, StringComparison.Ordinal)
                    || bundleId.StartsWith(prefix, StringComparison.Ordinal)))
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    /// <summary>
    /// Returns the declared entries with the selected primary entry first. The returned list is a new
    /// list even when the first entry is selected, so callers can safely project or enumerate it
    /// without mutating the manifest's list. Relative order of all non-selected entries is preserved.
    /// </summary>
    /// <exception cref="ArgumentException">The explicit primary value is blank.</exception>
    /// <exception cref="InvalidOperationException">The explicit primary does not resolve exactly one entry.</exception>
    public static IReadOnlyList<IncludedAppManifest> ProjectPrimaryFirst(DetectionManifest detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        var includedApps = detection.IncludedApps
            ?? throw new InvalidOperationException("Detection.IncludedApps is required for macOS bundle selection.");

        var primaryIndex = ResolvePrimaryIndex(detection.PrimaryBundleId, includedApps);
        if (primaryIndex == 0)
        {
            return includedApps.ToArray();
        }

        var projected = new List<IncludedAppManifest>(includedApps.Count) { includedApps[primaryIndex] };
        for (var index = 0; index < includedApps.Count; index++)
        {
            if (index != primaryIndex)
            {
                projected.Add(includedApps[index]);
            }
        }

        return projected;
    }

    /// <summary>Resolves the selected entry index using the same rules as <see cref="ProjectPrimaryFirst"/>.</summary>
    public static int ResolvePrimaryIndex(
        string? primaryBundleId,
        IReadOnlyList<IncludedAppManifest> includedApps)
    {
        ArgumentNullException.ThrowIfNull(includedApps);

        if (primaryBundleId is null)
        {
            if (includedApps.Count == 0)
            {
                throw new InvalidOperationException("Detection.IncludedApps must contain at least one entry.");
            }

            return 0;
        }

        if (string.IsNullOrWhiteSpace(primaryBundleId))
        {
            throw new ArgumentException("PrimaryBundleId must not be blank.", nameof(primaryBundleId));
        }

        var matches = FindMatchingIndexes(primaryBundleId, includedApps);
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"PrimaryBundleId '{primaryBundleId}' did not match any IncludedApps BundleId."),
            _ => throw new InvalidOperationException(
                $"PrimaryBundleId '{primaryBundleId}' matched multiple IncludedApps BundleId values."),
        };
    }
}
