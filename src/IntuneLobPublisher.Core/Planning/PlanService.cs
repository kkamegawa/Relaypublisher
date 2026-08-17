using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Planning;

/// <summary>Options of a plan run.</summary>
/// <param name="RepositoryRoot">Repository root directory.</param>
/// <param name="ManifestRoot">Directory (relative to the repository root) holding the manifests.</param>
/// <param name="BaseRef">Git ref/sha to diff against; null selects all manifests.</param>
/// <param name="ExplicitManifests">Explicit manifest paths overriding diff-based detection.</param>
public sealed record PlanOptions(
    string RepositoryRoot,
    string ManifestRoot,
    string? BaseRef,
    IReadOnlyList<string>? ExplicitManifests);

/// <summary>
/// Resolves the target manifest set once so later commands and CI jobs reuse the same set
/// (doc/00-overview.md 6.6). Changes under other paths (e.g. scripts/**) map back to the
/// manifests that reference them.
/// </summary>
public sealed class PlanService
{
    private readonly IManifestLoader _manifestLoader;
    private readonly IGitDiffRunner _gitDiffRunner;
    private readonly ILogger<PlanService> _logger;

    public PlanService(IManifestLoader manifestLoader, IGitDiffRunner gitDiffRunner, ILogger<PlanService> logger)
    {
        _manifestLoader = manifestLoader;
        _gitDiffRunner = gitDiffRunner;
        _logger = logger;
    }

    /// <summary>Returns repo-root-relative manifest paths (forward slashes, sorted).</summary>
    public async Task<IReadOnlyList<string>> ResolveTargetsAsync(PlanOptions options, CancellationToken cancellationToken)
    {
        var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);

        if (options.ExplicitManifests is { Count: > 0 })
        {
            return options.ExplicitManifests
                .Select(p => NormalizeRelative(repositoryRoot, p))
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        var allManifests = EnumerateManifests(repositoryRoot, options.ManifestRoot);

        if (options.BaseRef is null)
        {
            _logger.LogInformation("No base ref specified; selecting all {Count} manifests.", allManifests.Count);
            return allManifests;
        }

        var changedFiles = await _gitDiffRunner
            .GetChangedFilesAsync(repositoryRoot, options.BaseRef, cancellationToken)
            .ConfigureAwait(false);
        if (changedFiles is null)
        {
            _logger.LogWarning(
                "Base ref '{BaseRef}' could not be resolved; falling back to all {Count} manifests.",
                options.BaseRef, allManifests.Count);
            return allManifests;
        }

        var changedSet = changedFiles
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var manifestPath in allManifests)
        {
            if (changedSet.Contains(manifestPath))
            {
                targets.Add(manifestPath);
                continue;
            }

            if (await ReferencesChangedFileAsync(repositoryRoot, manifestPath, changedSet, cancellationToken)
                    .ConfigureAwait(false))
            {
                targets.Add(manifestPath);
            }
        }

        _logger.LogInformation(
            "Changed detection selected {TargetCount} of {TotalCount} manifests (base ref {BaseRef}).",
            targets.Count, allManifests.Count, options.BaseRef);
        return targets.ToList();
    }

    private async Task<bool> ReferencesChangedFileAsync(
        string repositoryRoot,
        string manifestPath,
        IReadOnlySet<string> changedSet,
        CancellationToken cancellationToken)
    {
        IntunePackageManifest manifest;
        try
        {
            manifest = await _manifestLoader
                .LoadAsync(Path.Combine(repositoryRoot, manifestPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ManifestLoadException ex)
        {
            // A broken manifest that was not itself changed is left for validate to report.
            _logger.LogWarning("Skipping unparsable manifest '{Manifest}' during reverse lookup: {Error}", manifestPath, ex.Message);
            return false;
        }

        return EnumerateReferencedFiles(manifest)
            .Select(p => p.Replace('\\', '/'))
            .Any(changedSet.Contains);
    }

    private static IEnumerable<string> EnumerateReferencedFiles(IntunePackageManifest manifest)
    {
        if (manifest.Icon is { } icon)
        {
            yield return icon;
        }

        foreach (var app in manifest.Apps)
        {
            if (app.Detection?.ScriptFile is { } scriptFile)
            {
                yield return scriptFile;
            }

            if (app.Scripts?.PreInstall is { } preInstall)
            {
                yield return preInstall;
            }

            if (app.Scripts?.PostInstall is { } postInstall)
            {
                yield return postInstall;
            }

            if (app.Package is { } package)
            {
                foreach (var file in package.RepositoryFiles)
                {
                    if (file.Source is { } source)
                    {
                        yield return source;
                    }
                }
            }
        }
    }

    private static List<string> EnumerateManifests(string repositoryRoot, string manifestRoot)
    {
        var manifestRootFull = Path.GetFullPath(Path.Combine(repositoryRoot, manifestRoot));
        if (!Directory.Exists(manifestRootFull))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(manifestRootFull, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Select(p => NormalizeRelative(repositoryRoot, p))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string NormalizeRelative(string repositoryRoot, string path)
    {
        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path);
        var relative = Path.GetRelativePath(repositoryRoot, Path.GetFullPath(fullPath)).Replace('\\', '/');

        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new UnsafePathException($"Manifest path '{path}' resolves outside the repository root '{repositoryRoot}'.");
        }

        return relative;
    }
}
