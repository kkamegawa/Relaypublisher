using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>A manifest together with the file path it was loaded from.</summary>
public sealed record LoadedManifest(string Path, IntunePackageManifest Manifest);

/// <summary>
/// Repository-wide lint across all manifests of a validate run.
/// PackageIdentifier + Platform + Architecture and DisplayName are the app identity
/// resolution keys, so duplicates would make Intune resolution ambiguous.
/// Multiple version folders of the same package (same identity, different PackageVersion)
/// are expected and not reported.
/// </summary>
public sealed class ManifestSetValidator
{
    public IReadOnlyList<string> Validate(IReadOnlyList<LoadedManifest> manifests)
    {
        var errors = new List<string>();
        var entries = manifests
            .SelectMany(m => m.Manifest.Apps
                .Where(a => a.PlatformArchitectureComplete())
                .Select(a => new AppEntry(
                    m.Path,
                    m.Manifest.PackageIdentifier ?? string.Empty,
                    m.Manifest.PackageVersion ?? string.Empty,
                    a.Platform!,
                    a.Architecture!,
                    a.DisplayName)))
            .ToList();

        foreach (var group in entries.GroupBy(e => e.IdentityKey, StringComparer.OrdinalIgnoreCase))
        {
            var versionCollisions = group
                .GroupBy(e => e.PackageVersion, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var collision in versionCollisions)
            {
                var paths = string.Join(", ", collision.Select(e => e.Path).Distinct());
                errors.Add(
                    $"Duplicate app identity '{group.Key}' (PackageVersion {collision.Key}) found in: {paths}. " +
                    "PackageIdentifier + Platform + Architecture must be unique.");
            }
        }

        foreach (var group in entries
                     .Where(e => !string.IsNullOrEmpty(e.DisplayName))
                     .GroupBy(e => e.DisplayName!, StringComparer.OrdinalIgnoreCase))
        {
            var identifiers = group
                .Select(e => e.PackageIdentifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (identifiers.Count > 1)
            {
                var paths = string.Join(", ", group.Select(e => e.Path).Distinct());
                errors.Add(
                    $"Duplicate DisplayName '{group.Key}' used by different packages ({string.Join(", ", identifiers)}) in: {paths}. " +
                    "DisplayName must be unique because it is the app resolution fallback key.");
            }
        }

        return errors;
    }

    private sealed record AppEntry(
        string Path,
        string PackageIdentifier,
        string PackageVersion,
        string Platform,
        string Architecture,
        string? DisplayName)
    {
        public string IdentityKey => $"{PackageIdentifier}|{Platform}|{Architecture}";
    }
}

file static class AppManifestExtensions
{
    public static bool PlatformArchitectureComplete(this AppManifest app)
        => !string.IsNullOrEmpty(app.Platform) && !string.IsNullOrEmpty(app.Architecture);
}
