using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Sources;

/// <summary>Request to download one source item to an already path-validated destination.</summary>
/// <param name="Source">The manifest source item.</param>
/// <param name="DestinationPath">Absolute file path inside the staging directory.</param>
public sealed record SourceDownloadRequest(SourceManifest Source, string DestinationPath);

/// <summary>Result of a completed download.</summary>
public sealed record DownloadedFile(string Path, long SizeBytes, string Sha256);

/// <summary>Downloads external files of one source Type.</summary>
public interface ISourceProvider
{
    /// <summary>The manifest source Type this provider handles, e.g. "publicHttp".</summary>
    string SourceType { get; }

    /// <exception cref="SourceDownloadException">The download failed.</exception>
    Task<DownloadedFile> DownloadAsync(SourceDownloadRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves the provider for a manifest source Type.</summary>
public sealed class SourceProviderRegistry
{
    private readonly Dictionary<string, ISourceProvider> _providers;

    public SourceProviderRegistry(IEnumerable<ISourceProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.SourceType, StringComparer.Ordinal);
    }

    public ISourceProvider Get(string sourceType)
        => _providers.TryGetValue(sourceType, out var provider)
            ? provider
            : throw new SourceDownloadException(
                $"No source provider is available for Type '{sourceType}'. Available types: {string.Join(", ", _providers.Keys)}.");
}
