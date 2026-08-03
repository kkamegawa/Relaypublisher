using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Reads manifest-referenced files at publish time. Shared by <see cref="WindowsAppPublisher"/> and <see cref="MacOsAppPublisher"/>.</summary>
internal static class ManifestAssetReader
{
    /// <summary>Reads the top-level <c>Icon</c> file, or null when the manifest has none.</summary>
    public static async Task<byte[]?> ReadIconAsync(PublishRequest request, IntunePackageManifest manifest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(manifest.Icon))
        {
            return null;
        }

        var iconPath = PathSafety.ResolveWithin(request.RepositoryRoot, manifest.Icon, "Icon");
        if (!File.Exists(iconPath))
        {
            throw new ManifestLoadException($"Icon '{manifest.Icon}' does not exist under '{request.RepositoryRoot}'.");
        }

        return await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(false);
    }
}
