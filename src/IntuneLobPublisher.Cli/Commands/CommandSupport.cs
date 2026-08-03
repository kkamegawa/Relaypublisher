using System.CommandLine;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>Exit codes shared by all commands.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int NotImplemented = 2;
}

/// <summary>Option factories and shared manifest workflows.</summary>
internal static class CommandSupport
{
    public static Option<string[]> ManifestOption() => new("--manifest", "-m")
    {
        Description = "Manifest file paths or glob patterns (repeatable).",
        AllowMultipleArgumentsPerToken = true,
    };

    public static Option<string?> ManifestListOption() => new("--manifest-list")
    {
        Description = "manifest-list.json produced by the plan command, used instead of --manifest.",
    };

    public static Option<string> RepoRootOption() => new("--repo-root")
    {
        Description = "Repository root directory manifest paths resolve against.",
        DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
    };

    public static Option<bool> VerboseOption() => new("--verbose")
    {
        Description = "Enables verbose logging.",
    };

    /// <summary>Resolves --manifest / --manifest-list inputs into concrete file paths.</summary>
    public static IReadOnlyList<string> ResolveManifestInputs(
        string repoRoot,
        string[] manifestPatterns,
        string? manifestListPath)
    {
        if (manifestListPath is not null)
        {
            return ManifestFileResolver.ReadManifestList(repoRoot, manifestListPath);
        }

        if (manifestPatterns.Length == 0)
        {
            throw new ManifestLoadException("Specify --manifest or --manifest-list.");
        }

        return ManifestFileResolver.ResolvePatterns(repoRoot, manifestPatterns);
    }

    /// <summary>
    /// Loads and validates the given manifest files, including file-backed asset checks (issue #63)
    /// and the repository-wide uniqueness lint. Returns the successfully loaded manifests and all errors.
    /// </summary>
    public static async Task<(IReadOnlyList<LoadedManifest> Manifests, IReadOnlyList<string> Errors)> LoadAndValidateAsync(
        IServiceProvider services,
        IReadOnlyList<string> manifestFiles,
        string repoRoot,
        CancellationToken cancellationToken)
    {
        var loader = services.GetRequiredService<IManifestLoader>();
        var validator = services.GetRequiredService<IManifestValidator>();
        var setValidator = services.GetRequiredService<ManifestSetValidator>();

        var loaded = new List<LoadedManifest>();
        var errors = new List<string>();

        foreach (var file in manifestFiles)
        {
            try
            {
                var manifest = await loader.LoadAsync(file, cancellationToken);
                var result = validator.Validate(manifest);
                if (!result.IsValid)
                {
                    errors.AddRange(result.Errors.Select(e => $"{file}: {e.PropertyName}: {e.ErrorMessage}"));
                    continue;
                }

                var assetErrors = ManifestAssetValidator.Validate(manifest, repoRoot);
                if (assetErrors.Count > 0)
                {
                    errors.AddRange(assetErrors.Select(e => $"{file}: {e}"));
                    continue;
                }

                loaded.Add(new LoadedManifest(file, manifest));
            }
            catch (ManifestLoadException ex)
            {
                errors.Add(ex.Message);
            }
        }

        errors.AddRange(setValidator.Validate(loaded));
        return (loaded, errors);
    }

    public static int ReportErrors(IReadOnlyList<string> errors)
    {
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }

        Console.Error.WriteLine($"{errors.Count} error(s) found.");
        return ExitCodes.Failure;
    }
}
