using System.CommandLine;
using Azure.Identity;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>`publish` pushes packaged apps to Microsoft Intune: app metadata, content and assignments.</summary>
internal static class PublishCommand
{
    public static Command Create(IServiceProvider services)
    {
        var manifestOption = CommandSupport.ManifestOption();
        var manifestListOption = CommandSupport.ManifestListOption();
        var repoRootOption = CommandSupport.RepoRootOption();
        var verboseOption = CommandSupport.VerboseOption();
        var packageDirOption = new Option<string>("--package-dir")
        {
            Description = "Directory produced by the package command.",
            Required = true,
        };
        var expectedTenantOption = new Option<string?>("--expected-tenant")
        {
            Description = "Tenant id the Graph token's `tid` claim must match; publishing fails on mismatch.",
        };
        var allowDowngradeOption = new Option<bool>("--allow-downgrade")
        {
            Description = "Publish even when the manifest version is lower than the published version (default: skip + warning).",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would change, including the assignment plan (new apps show the placeholder id '(new app)'), without writing to Intune.",
        };
        var sourceCommitOption = new Option<string?>("--source-commit")
        {
            Description = "Commit SHA recorded in management metadata. Defaults to GITHUB_SHA or BUILD_SOURCEVERSION.",
        };

        var command = new Command("publish", "Publishes staged packages to Microsoft Intune.");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(packageDirOption);
        command.Options.Add(expectedTenantOption);
        command.Options.Add(allowDowngradeOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(sourceCommitOption);
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
                    Console.WriteLine("No manifests to publish.");
                    return ExitCodes.Success;
                }

                var (manifests, errors) = await CommandSupport.LoadAndValidateAsync(services, files, cancellationToken);
                if (errors.Count > 0)
                {
                    return CommandSupport.ReportErrors(errors);
                }

                var entries = SelectHighestVersions(manifests);
                if (entries.Count == 0)
                {
                    Console.WriteLine("No app entries to publish.");
                    return ExitCodes.Success;
                }

                var graphOptions = new GraphClientOptions
                {
                    ExpectedTenantId = parseResult.GetValue(expectedTenantOption),
                };
                var sourceCommit = parseResult.GetValue(sourceCommitOption)
                    ?? Environment.GetEnvironmentVariable("GITHUB_SHA")
                    ?? Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION")
                    ?? "unknown";
                var packageDirectory = parseResult.GetValue(packageDirOption)!;
                var allowDowngrade = parseResult.GetValue(allowDowngradeOption);
                var dryRun = parseResult.GetValue(dryRunOption);

                using var composition = PublishComposition.Create(
                    graphOptions, services.GetRequiredService<ILoggerFactory>());

                return await PublishEntriesAsync(
                    composition.Orchestrator, entries, repoRoot, packageDirectory,
                    sourceCommit, allowDowngrade, dryRun, cancellationToken);
            }
            catch (PublisherException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    private sealed record PublishEntry(LoadedManifest Loaded, AppManifest App);

    /// <summary>
    /// When several manifests target the same app identity, only the highest PackageVersion is
    /// published (doc/00-overview.md 6.8); the rest are reported as skipped.
    /// </summary>
    private static List<PublishEntry> SelectHighestVersions(IReadOnlyList<LoadedManifest> manifests)
    {
        var byIdentity = new Dictionary<AppIdentity, PublishEntry>();
        foreach (var loaded in manifests)
        {
            foreach (var app in loaded.Manifest.Apps)
            {
                var identity = new AppIdentity(
                    loaded.Manifest.PackageIdentifier!, app.Platform!, app.Architecture!);
                if (!byIdentity.TryGetValue(identity, out var existing))
                {
                    byIdentity[identity] = new PublishEntry(loaded, app);
                    continue;
                }

                var candidate = new PublishEntry(loaded, app);
                var comparison = PublishGuard.CompareVersions(
                    loaded.Manifest.PackageVersion!, existing.Loaded.Manifest.PackageVersion!);
                if (comparison == 0)
                {
                    // Equal versions for the same identity: keep whichever was seen first so
                    // selection does not depend on dictionary iteration order, and say so plainly
                    // instead of the misleading "superseded" wording.
                    Console.WriteLine(
                        $"Skipping {identity.PackageIdentifier} {identity.Platform}-{identity.Architecture} " +
                        $"duplicate version {loaded.Manifest.PackageVersion} from '{loaded.Path}' " +
                        $"(version {existing.Loaded.Manifest.PackageVersion} from '{existing.Loaded.Path}' was already selected).");
                    continue;
                }

                var (winner, loser) = comparison > 0 ? (candidate, existing) : (existing, candidate);
                byIdentity[identity] = winner;
                Console.WriteLine(
                    $"Skipping {identity.PackageIdentifier} {identity.Platform}-{identity.Architecture} " +
                    $"version {loser.Loaded.Manifest.PackageVersion} from '{loser.Loaded.Path}' " +
                    $"(superseded by version {winner.Loaded.Manifest.PackageVersion}).");
            }
        }

        return [.. byIdentity.Values];
    }

    /// <summary>
    /// Publishes entries one by one, continuing on per-app failures so one broken app does not block
    /// the rest of a CI batch (reruns converge, doc/00-overview.md 6.10). Tenant mismatch and
    /// authentication failures abort the whole run — nothing else can succeed after those.
    /// </summary>
    private static async Task<int> PublishEntriesAsync(
        IPublishOrchestrator orchestrator,
        List<PublishEntry> entries,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var published = 0;
        var skippedDowngrade = 0;
        var skippedPlatform = 0;
        var failed = 0;

        foreach (var entry in entries)
        {
            var label = $"{entry.Loaded.Manifest.PackageIdentifier} {entry.App.Platform}-{entry.App.Architecture}";
            var request = new PublishRequest(
                entry.Loaded.Manifest,
                entry.App,
                Path.GetRelativePath(repoRoot, entry.Loaded.Path).Replace('\\', '/'),
                repoRoot,
                packageDirectory,
                sourceCommit,
                allowDowngrade,
                dryRun);

            try
            {
                var result = await orchestrator.PublishAsync(
                    request,
                    plan => Console.Write(AssignmentPlanFormatter.Format(plan)),
                    cancellationToken);

                switch (result.Outcome)
                {
                    case PublishOutcome.Published:
                        published++;
                        Console.WriteLine($"Published {label} -> app {result.AppId} (content: {result.ContentOutcome}).");
                        break;
                    case PublishOutcome.DryRunCompleted:
                        Console.WriteLine($"[dry-run] {label} -> app {result.AppId ?? PublishOrchestrator.NewAppPlaceholderId}.");
                        break;
                    case PublishOutcome.SkippedDowngrade:
                        skippedDowngrade++;
                        Console.WriteLine($"Skipped {label}: {result.SkipReason}");
                        break;
                    case PublishOutcome.SkippedPlatformNotSupported:
                        skippedPlatform++;
                        Console.WriteLine($"Skipped {label}: {result.SkipReason}");
                        break;
                }
            }
            catch (TenantMismatchException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (AuthenticationFailedException ex)
            {
                Console.Error.WriteLine($"error: Graph authentication failed: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (PublisherException ex)
            {
                failed++;
                Console.Error.WriteLine($"error: {label}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"{published} published, {skippedDowngrade} skipped (downgrade), " +
            $"{skippedPlatform} skipped (platform), {failed} failed.");
        return failed == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }
}
