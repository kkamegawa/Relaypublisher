using System.CommandLine;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.DependencyInjection;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>`package` validates manifests, stages Windows Win32 app packages and generates .intunewin files.</summary>
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
        var stageOnlyOption = new Option<bool>("--stage-only")
        {
            Description = "Stage package files without generating .intunewin.",
        };
        var toolPathOption = new Option<string?>("--intunewin-tool")
        {
            Description = "Path to IntuneWinAppUtil.exe. Overrides the " +
                $"{IntuneWinToolResolver.ToolPathEnvironmentVariable} environment variable and the tools directory.",
        };
        var toolVersionOption = new Option<string?>("--intunewin-tool-version")
        {
            Description = "IntuneWinAppUtil release tag to pin. When omitted the latest official release is used.",
        };
        var toolSha256Option = new Option<string?>("--intunewin-tool-sha256")
        {
            Description = "Known-good SHA256 of IntuneWinAppUtil.exe. Packaging fails when the tool does not match.",
        };
        var toolsDirectoryOption = new Option<string?>("--tools-directory")
        {
            Description = "Directory downloaded tools are cached under. Defaults to <repo-root>/tools.",
        };

        var command = new Command("package", "Stages Windows Win32 app package files and generates .intunewin packages.");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(outputOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(stageOnlyOption);
        command.Options.Add(toolPathOption);
        command.Options.Add(toolVersionOption);
        command.Options.Add(toolSha256Option);
        command.Options.Add(toolsDirectoryOption);
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

                var dryRun = parseResult.GetValue(dryRunOption);
                var stageOnly = parseResult.GetValue(stageOnlyOption);
                var generateIntuneWin = !dryRun && !stageOnly;
                if (generateIntuneWin && !OperatingSystem.IsWindows())
                {
                    Console.Error.WriteLine(
                        "error: .intunewin generation requires Windows because IntuneWinAppUtil.exe is a Windows executable. " +
                        "Use --stage-only on other platforms.");
                    return ExitCodes.Failure;
                }

                var stagingService = services.GetRequiredService<IWindowsStagingService>();
                var options = new StagingOptions(
                    repoRoot,
                    parseResult.GetValue(outputOption)!,
                    dryRun);
                var toolOptions = new IntuneWinToolOptions(
                    parseResult.GetValue(toolPathOption),
                    parseResult.GetValue(toolVersionOption),
                    parseResult.GetValue(toolSha256Option),
                    parseResult.GetValue(toolsDirectoryOption) ?? Path.Combine(repoRoot, "tools"));
                var packager = services.GetRequiredService<IIntuneWinPackager>();

                foreach (var loaded in manifests)
                {
                    foreach (var app in loaded.Manifest.Apps)
                    {
                        var result = await stagingService.StageAsync(loaded.Manifest, app, options, cancellationToken);
                        Console.WriteLine(result.DryRun
                            ? $"[dry-run] {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}"
                            : $"Staged {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}");

                        if (generateIntuneWin)
                        {
                            var package = await packager.CreatePackageAsync(
                                loaded.Manifest, result, toolOptions, cancellationToken);
                            Console.WriteLine(
                                $"Packaged {package.PackageIdentifier} {package.Platform}-{package.Architecture} -> " +
                                $"{package.IntuneWinPath} (inputHash {package.InputHash})");
                        }
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
