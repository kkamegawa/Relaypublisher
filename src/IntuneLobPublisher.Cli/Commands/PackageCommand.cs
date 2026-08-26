using System.CommandLine;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.DependencyInjection;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// `package` validates manifests, stages app package files and generates the final content artifact:
/// a `.intunewin` for Windows Win32 apps, or a staged `.pkg` plus its package-metadata.json for macOS
/// (macOS has no separate content-generation tool, doc/00-overview.md 6.13 / Phase 8).
/// </summary>
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
        var forceOption = CommandSupport.ForceOption();

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
        command.Options.Add(forceOption);
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

                var (manifests, errors) = await CommandSupport.LoadAndValidateAsync(services, files, repoRoot, cancellationToken);
                if (errors.Count > 0)
                {
                    return CommandSupport.ReportErrors(errors);
                }

                var dryRun = parseResult.GetValue(dryRunOption);
                var stageOnly = parseResult.GetValue(stageOnlyOption);
                var generatePackageArtifact = !dryRun && !stageOnly;

                // Only Windows entries need IntuneWinAppUtil.exe; a macOS-only manifest list still
                // packages on Linux/macOS runners, since macOS packaging has no external tool step.
                var hasWindowsApp = manifests.Any(m => m.Manifest.Apps.Any(a => a.Platform == "windows"));
                if (generatePackageArtifact && hasWindowsApp && !OperatingSystem.IsWindows())
                {
                    Console.Error.WriteLine(
                        "error: .intunewin generation requires Windows because IntuneWinAppUtil.exe is a Windows executable. " +
                        "Use --stage-only on other platforms.");
                    return ExitCodes.Failure;
                }

                var windowsStagingService = services.GetRequiredService<IWindowsStagingService>();
                var macOsStagingService = services.GetRequiredService<IMacOsStagingService>();
                var options = new StagingOptions(
                    repoRoot,
                    parseResult.GetValue(outputOption)!,
                    dryRun);
                var toolOptions = new IntuneWinToolOptions(
                    parseResult.GetValue(toolPathOption),
                    parseResult.GetValue(toolVersionOption),
                    parseResult.GetValue(toolSha256Option),
                    parseResult.GetValue(toolsDirectoryOption) ?? Path.Combine(repoRoot, "tools"));
                var windowsPackager = services.GetRequiredService<IIntuneWinPackager>();
                var macOsPackager = services.GetRequiredService<IMacOsPackager>();
                var force = parseResult.GetValue(forceOption);

                // Collected across every macOS entry so the operator sees the whole batch's semantic
                // warnings and confirms once, not once per entry (doc/05-operation.md).
                var warnedEntries = new List<(string Label, string MetadataPath, IReadOnlyList<PkgInspectionWarning> Warnings)>();

                foreach (var loaded in manifests)
                {
                    foreach (var app in loaded.Manifest.Apps)
                    {
                        if (app.Platform == "macos")
                        {
                            var macResult = await macOsStagingService.StageAsync(loaded.Manifest, app, options, cancellationToken);
                            Console.WriteLine(macResult.DryRun
                                ? $"[dry-run] {macResult.PackageIdentifier} {macResult.Platform}-{macResult.Architecture} -> {macResult.StagingDirectory}"
                                : $"Staged {macResult.PackageIdentifier} {macResult.Platform}-{macResult.Architecture} -> {macResult.StagingDirectory}");

                            if (generatePackageArtifact)
                            {
                                var macPackage = await macOsPackager.CreatePackageAsync(
                                    loaded.Manifest, macResult, cancellationToken, forceAcknowledged: force);
                                Console.WriteLine(
                                    $"Packaged {macPackage.PackageIdentifier} {macPackage.Platform}-{macPackage.Architecture} -> " +
                                    $"{macPackage.ContentPath} (inputHash {macPackage.InputHash})");

                                if (macPackage.Inspection is { Warnings.Count: > 0 } inspection)
                                {
                                    warnedEntries.Add((
                                        $"{macPackage.PackageIdentifier} {macPackage.Platform}-{macPackage.Architecture}",
                                        macPackage.MetadataPath,
                                        inspection.Warnings));
                                }
                            }

                            continue;
                        }

                        var result = await windowsStagingService.StageAsync(loaded.Manifest, app, options, cancellationToken);
                        Console.WriteLine(result.DryRun
                            ? $"[dry-run] {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}"
                            : $"Staged {result.PackageIdentifier} {result.Platform}-{result.Architecture} -> {result.StagingDirectory}");

                        if (generatePackageArtifact)
                        {
                            var package = await windowsPackager.CreatePackageAsync(
                                loaded.Manifest, result, toolOptions, cancellationToken);
                            Console.WriteLine(
                                $"Packaged {package.PackageIdentifier} {package.Platform}-{package.Architecture} -> " +
                                $"{package.IntuneWinPath} (inputHash {package.InputHash})");
                        }
                    }
                }

                if (warnedEntries.Count > 0)
                {
                    Console.Write(PkgInspectionWarningFormatter.FormatBatch(
                        [.. warnedEntries.Select(e => (e.Label, e.Warnings))]));

                    var interactive = SemanticWarningGate.IsInteractive(
                        () => Console.IsInputRedirected, () => Console.IsOutputRedirected);
                    var decision = SemanticWarningGate.Decide(
                        hasWarnings: true,
                        force,
                        interactive,
                        () => SemanticWarningGate.ConfirmOnConsole(Console.In, Console.Out));

                    switch (decision)
                    {
                        case WarningGateDecision.ForceAcknowledged:
                            Console.WriteLine("Semantic PKG inspection warnings acknowledged via --force.");
                            break;
                        case WarningGateDecision.Acknowledged:
                            Console.WriteLine("Semantic PKG inspection warnings acknowledged.");
                            break;
                        case WarningGateDecision.ForceRequired:
                            DeleteWarnedMetadata(warnedEntries);
                            Console.Error.WriteLine(
                                "error: semantic PKG inspection warnings were found in a non-interactive run. " +
                                "Re-run with --force to acknowledge them, or fix the manifest/PKG mismatch.");
                            return ExitCodes.Failure;
                        case WarningGateDecision.Declined:
                            DeleteWarnedMetadata(warnedEntries);
                            Console.Error.WriteLine("error: semantic PKG inspection warnings were not acknowledged.");
                            return ExitCodes.Failure;
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

    /// <summary>
    /// Deletes package-metadata.json for every entry whose semantic warnings were not acknowledged, so
    /// the entry fails closed: a missing metadata file makes both a rerun of <c>package</c> and any
    /// subsequent <c>publish</c> preflight fail loudly instead of silently trusting stale artifacts.
    /// </summary>
    private static void DeleteWarnedMetadata(
        IReadOnlyList<(string Label, string MetadataPath, IReadOnlyList<PkgInspectionWarning> Warnings)> warnedEntries)
    {
        foreach (var entry in warnedEntries)
        {
            try
            {
                File.Delete(entry.MetadataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"warning: could not remove unacknowledged package metadata '{entry.MetadataPath}': {ex.Message}");
            }
        }
    }
}
