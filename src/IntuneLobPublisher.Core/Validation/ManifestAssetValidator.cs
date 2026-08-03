using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>
/// Validates manifest-referenced files that require repository-root file system access
/// (existence, size), which <see cref="ManifestValidator"/> cannot check on its own since it has
/// no repository root. Currently covers <see cref="IntunePackageManifest.Icon"/>
/// (issue #63): format is checked by <see cref="ManifestValidator"/>, existence and size here, so
/// a missing/oversized icon fails before any Graph call rather than during publish.
/// </summary>
public static class ManifestAssetValidator
{
    /// <summary>Returns error strings (empty when valid) for the given manifest's file-backed assets.</summary>
    public static IReadOnlyList<string> Validate(IntunePackageManifest manifest, string repositoryRoot)
    {
        if (manifest.Icon is null)
        {
            return [];
        }

        // Path safety (traversal, absolute paths) is already checked by ManifestValidator before this
        // runs; ResolveWithin re-validates defensively rather than trusting an already-loaded manifest.
        string iconPath;
        try
        {
            iconPath = PathSafety.ResolveWithin(repositoryRoot, manifest.Icon, "Icon");
        }
        catch (UnsafePathException ex)
        {
            return [ex.Message];
        }

        if (!File.Exists(iconPath))
        {
            return [$"Icon '{manifest.Icon}' does not exist under '{repositoryRoot}'."];
        }

        var sizeBytes = new FileInfo(iconPath).Length;
        if (sizeBytes > ManifestValues.MaxIconBytes)
        {
            return [$"Icon '{manifest.Icon}' is {sizeBytes} bytes, which exceeds the maximum of {ManifestValues.MaxIconBytes} bytes."];
        }

        return [];
    }
}
