using System.CommandLine;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Planning;
using Microsoft.Extensions.DependencyInjection;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// `plan` resolves the target manifest set once and writes manifest-list.json so
/// later commands and CI jobs reuse the same set instead of recomputing it.
/// </summary>
internal static class PlanCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static Command Create(IServiceProvider services)
    {
        var baseRefOption = new Option<string?>("--base-ref")
        {
            Description = "Git ref/sha to diff against. Falls back to all manifests when missing or unresolvable.",
        };
        var manifestsOption = new Option<string[]>(new[] { "--manifest", "--manifests", "-m" })
        {
            Description = "Explicit manifest paths; overrides diff-based detection.",
            AllowMultipleArgumentsPerToken = true,
        };
        var manifestRootOption = new Option<string>("--manifest-root")
        {
            Description = "Directory (relative to --repo-root) that holds the manifests.",
            DefaultValueFactory = _ => "manifests",
        };
        var repoRootOption = CommandSupport.RepoRootOption();
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output path for manifest-list.json.",
            Required = true,
        };
        var verboseOption = CommandSupport.VerboseOption();

        var command = new Command("plan", "Resolves the target manifest set and writes manifest-list.json.");
        command.Options.Add(baseRefOption);
        command.Options.Add(manifestsOption);
        command.Options.Add(manifestRootOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var planService = services.GetRequiredService<PlanService>();
                var explicitManifests = parseResult.GetValue(manifestsOption) ?? [];
                var targets = await planService.ResolveTargetsAsync(
                    new PlanOptions(
                        parseResult.GetValue(repoRootOption)!,
                        parseResult.GetValue(manifestRootOption)!,
                        parseResult.GetValue(baseRefOption),
                        explicitManifests.Length > 0 ? explicitManifests : null),
                    cancellationToken);

                var outputPath = parseResult.GetValue(outputOption)!;
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(
                    outputPath,
                    JsonSerializer.Serialize(new { Manifests = targets }, JsonOptions),
                    cancellationToken);

                Console.WriteLine($"{targets.Count} manifest(s) written to {outputPath}.");
                return ExitCodes.Success;
            }
            catch (PublisherException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }
}
