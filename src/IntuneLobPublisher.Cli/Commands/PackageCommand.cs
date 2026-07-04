using System.CommandLine;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.DependencyInjection;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>`package` validates manifests and stages Windows Win32 app packages.</summary>
internal static class PackageCommand
{
    public static Command Create(IServiceProvider services)
    {
        var manifestOption = CommandSupport.ManifestOption();
        var manifestListOption = CommandSupport.ManifestListOption();
        var repoRootOption = CommandSupport.RepoRootOption();
        var verboseOption = CommandSupport.VerboseOption();
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for staged packages.",
            Required = true,
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would happen without copying or downloading files.",
        };

        var command = new Command("package", "Stages Windows Win32 app package files.");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var repoRoot = parseResult.GetValue(repoRootOption)!;
                var files = CommandSupport.ResolveManifestInputs(
                    repoRoot,
                    parseResult.GetValue(manifestOption) ?? [],
                    parseResult.GetValue(manifestListOption));
                if (files.Count == 0)
                {
                    Console.WriteLine("No manifests to package.");
                    return ExitCodes.Success;
                }

                var (manifests, errors) = await CommandSupport.LoadAndValidateAsync(services, files, cancellationToken);
                if (errors.Count > 0)
                {
                    return CommandSupport.ReportErrors(errors);
                }

                var stagingService = services.GetRequiredService<IWindowsStagingService>();
                var options = new StagingOptions(
                    repoRoot,
                    parseResult.GetValue(outputOption)!,
                    parseResult.GetValue(dryRunOption));

                foreach (var loaded in manifests)
                {
                    foreach (var app in loaded.Manifest.Apps)
                    {
                        var result = await stagingService.StageAsync(loaded.Manifest, app, options, cancellationToken);
                        Console.WriteLine(result.DryRun
                            ? $"[dry-run] {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}"
                            : $"Staged {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}");
                    }
                }

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
