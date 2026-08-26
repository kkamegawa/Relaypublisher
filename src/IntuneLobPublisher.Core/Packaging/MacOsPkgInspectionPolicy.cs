using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Reconciles facts from a bounded PKG inspection with the manifest's declared detection entries.
/// The XAR parser remains manifest-independent; this policy is the small, deterministic boundary
/// that produces the report persisted with a package artifact.
/// </summary>
public static class MacOsPkgInspectionPolicy
{
    public static PkgInspectionReport CreateReport(
        IntunePackageManifest manifest,
        AppManifest app,
        PkgBundleInspectionResult inspection,
        bool forceAcknowledged = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(inspection);

        var detection = app.Detection
            ?? throw new PkgInspectionException("macOS app has no Detection block for PKG inspection.");
        var includedApps = detection.IncludedApps
            ?? throw new PkgInspectionException("macOS app has no Detection.IncludedApps for PKG inspection.");

        IReadOnlyList<IncludedAppManifest> projected;
        try
        {
            projected = MacOsBundleSelector.ProjectPrimaryFirst(detection);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new PkgInspectionException(
                "The macOS detection primary bundle cannot be resolved for PKG inspection.", exception);
        }

        var selectedPrimaryBundleId = projected.FirstOrDefault()?.BundleId;
        var actualById = inspection.Bundles
            .GroupBy(bundle => bundle.BundleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var warnings = new List<PkgInspectionWarning>();

        if (inspection.Bundles.Count > 1 && string.IsNullOrWhiteSpace(detection.PrimaryBundleId))
        {
            warnings.Add(new PkgInspectionWarning(
                PkgInspectionWarningCode.MultipleBundlesWithoutExplicitPrimary,
                selectedPrimaryBundleId,
                "The package declares multiple bundles and the first manifest entry is used as primary."));
        }

        foreach (var declared in includedApps)
        {
            if (string.IsNullOrWhiteSpace(declared.BundleId))
            {
                // Static validation reports this before package. Keep this as a hard artifact error
                // if a caller uses the policy independently of the CLI validation pipeline.
                throw new PkgInspectionException("A macOS detection entry has no BundleId.");
            }

            if (!actualById.TryGetValue(declared.BundleId, out var actual))
            {
                warnings.Add(new PkgInspectionWarning(
                    PkgInspectionWarningCode.ManifestBundleNotFound,
                    declared.BundleId,
                    "The manifest bundle is not declared by the PKG metadata."));
                continue;
            }

            var versionMismatch = !string.IsNullOrWhiteSpace(declared.BundleVersion)
                && !string.Equals(declared.BundleVersion, actual.BundleVersion, StringComparison.Ordinal);
            var buildMismatch = string.Equals(app.Platform, "macos", StringComparison.OrdinalIgnoreCase)
                && string.Equals(app.AppType, "lob", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(declared.BundleBuildVersion)
                && !string.Equals(declared.BundleBuildVersion, actual.BundleBuildVersion, StringComparison.Ordinal);
            if (versionMismatch || buildMismatch)
            {
                var details = new List<string>(2);
                if (versionMismatch)
                {
                    details.Add(
                        $"BundleVersion '{declared.BundleVersion}' does not match CFBundleShortVersionString '{actual.BundleVersion}'.");
                }

                if (buildMismatch)
                {
                    details.Add(
                        $"BundleBuildVersion '{declared.BundleBuildVersion}' does not match CFBundleVersion '{actual.BundleBuildVersion}'.");
                }

                warnings.Add(new PkgInspectionWarning(
                    PkgInspectionWarningCode.ManifestBundleVersionMismatch,
                    declared.BundleId,
                    string.Join(" ", details)));
            }
        }

        var declaredIds = includedApps
            .Where(entry => !string.IsNullOrWhiteSpace(entry.BundleId))
            .Select(entry => entry.BundleId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var actual in inspection.Bundles.Where(bundle => !declaredIds.Contains(bundle.BundleId)))
        {
            warnings.Add(new PkgInspectionWarning(
                PkgInspectionWarningCode.PackageBundleNotDeclared,
                actual.BundleId,
                "The package declares an application bundle that is not listed in Detection.IncludedApps."));
        }

        return new PkgInspectionReport(
            inspection,
            selectedPrimaryBundleId,
            warnings,
            forceAcknowledged);
    }
}
