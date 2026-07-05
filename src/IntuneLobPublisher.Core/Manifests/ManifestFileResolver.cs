using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Resolves --manifest arguments (paths or glob patterns) and --manifest-list files
/// into concrete manifest file paths.
/// </summary>
public static class ManifestFileResolver
{
    /// <summary>Expands paths and glob patterns relative to <paramref name="rootDirectory"/>.</summary>
    public static IReadOnlyList<string> ResolvePatterns(string rootDirectory, IReadOnlyList<string> patterns)
    {
        var results = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (pattern.IndexOfAny(['*', '?']) < 0)
            {
                var fullPath = Path.GetFullPath(Path.IsPathRooted(pattern)
                    ? pattern
                    : Path.Combine(rootDirectory, pattern));
                if (!File.Exists(fullPath))
                {
                    throw new ManifestLoadException($"Manifest file '{pattern}' does not exist.");
                }

                results.Add(fullPath);
                continue;
            }

            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern.Replace('\\', '/'));
            var matches = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootDirectory)));

            var matchedAny = false;
            foreach (var match in matches.Files)
            {
                matchedAny = true;
                results.Add(Path.GetFullPath(Path.Combine(rootDirectory, match.Path)));
            }

            if (!matchedAny)
            {
                throw new ManifestLoadException($"Manifest pattern '{pattern}' did not match any files under '{rootDirectory}'.");
            }
        }

        return results.ToList();
    }

    /// <summary>Reads a manifest-list.json produced by the plan command.</summary>
    public static IReadOnlyList<string> ReadManifestList(string rootDirectory, string manifestListPath)
    {
        var fullListPath = Path.GetFullPath(Path.IsPathRooted(manifestListPath)
            ? manifestListPath
            : Path.Combine(rootDirectory, manifestListPath));
        if (!File.Exists(fullListPath))
        {
            throw new ManifestLoadException($"Manifest list '{manifestListPath}' does not exist.");
        }

        ManifestList? list;
        try
        {
            list = JsonSerializer.Deserialize<ManifestList>(
                File.ReadAllText(fullListPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ManifestLoadException($"Manifest list '{manifestListPath}' is not valid JSON: {ex.Message}", ex);
        }

        if (list?.Manifests is not { Count: > 0 })
        {
            return [];
        }

        return list.Manifests
            .Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(rootDirectory, p)))
            .ToList();
    }

    /// <summary>Shape of manifest-list.json.</summary>
    public sealed class ManifestList
    {
        public List<string> Manifests { get; set; } = [];
    }
}
