using IntuneLobPublisher.Core.Exceptions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace IntuneLobPublisher.Core.Manifests;

/// <summary>YamlDotNet based manifest loader.</summary>
public sealed class ManifestLoader : IManifestLoader
{
    // Unknown keys are tolerated so that additive minor schema changes stay loadable;
    // incompatible changes are gated by the SchemaVersion major check during validation.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<IntunePackageManifest> LoadAsync(string path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ManifestLoadException($"Failed to read manifest file '{path}': {ex.Message}", ex);
        }

        IntunePackageManifest? manifest;
        try
        {
            manifest = _deserializer.Deserialize<IntunePackageManifest>(text);
        }
        catch (YamlException ex)
        {
            throw new ManifestLoadException($"Failed to parse manifest '{path}': {ex.Message}", ex);
        }

        return manifest ?? throw new ManifestLoadException($"Manifest '{path}' is empty.");
    }
}
