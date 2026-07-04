namespace IntuneLobPublisher.Core.Manifests;

/// <summary>Loads a package manifest from a YAML file.</summary>
public interface IManifestLoader
{
    /// <summary>Loads and deserializes the manifest at <paramref name="path"/>.</summary>
    /// <exception cref="Exceptions.ManifestLoadException">The file cannot be read or is not valid YAML.</exception>
    Task<IntunePackageManifest> LoadAsync(string path, CancellationToken cancellationToken);
}
