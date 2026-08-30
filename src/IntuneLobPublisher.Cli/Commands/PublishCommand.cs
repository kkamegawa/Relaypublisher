using System.CommandLine;
using IntuneLobPublisher.Core;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Publishing.Categories;
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
            Description = "Show what would change, including the category and assignment plans (new apps show the placeholder id '(new app)'), without writing to Intune.",
        };
        var sourceCommitOption = new Option<string?>("--source-commit")
        {
            Description = "Commit SHA recorded in management metadata. Defaults to GITHUB_SHA or BUILD_SOURCEVERSION.",
        };
        var resultFileOption = new Option<string?>("--result-file")
        {
            Description = "Writes a machine-readable JSON array with one result entry per published app entry.",
        };
        var forceOption = CommandSupport.ForceOption();

        var command = new Command("publish", "Publishes staged packages to Microsoft Intune.");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(packageDirOption);
        command.Options.Add(expectedTenantOption);
        command.Options.Add(allowDowngradeOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(sourceCommitOption);
        command.Options.Add(resultFileOption);
        command.Options.Add(forceOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var repoRoot = parseResult.GetValue(repoRootOption)!;
                var resultFile = parseResult.GetValue(resultFileOption);
                var files = CommandSupport.ResolveManifestInputs(
                    repoRoot,
                    parseResult.GetValue(manifestOption) ?? [],
                    parseResult.GetValue(manifestListOption));
                if (files.Count == 0)
                {
                    await WriteResultFileAsync(resultFile, [], cancellationToken);
                    Console.WriteLine("No manifests to publish.");
                    return ExitCodes.Success;
                }

                var (manifests, errors) = await CommandSupport.LoadAndValidateAsync(services, files, repoRoot, cancellationToken);
                if (errors.Count > 0)
                {
                    await WriteResultFileAsync(resultFile, [], cancellationToken);
                    return CommandSupport.ReportErrors(errors);
                }

                var entries = SelectHighestVersions(manifests);
                if (entries.Count == 0)
                {
                    await WriteResultFileAsync(resultFile, [], cancellationToken);
                    Console.WriteLine("No app entries to publish.");
                    return ExitCodes.Success;
                }

                ValidateResultFileDirectory(resultFile);

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
                var force = parseResult.GetValue(forceOption);

                // Layer 3 preflight (issue #116): every entry's artifact is re-verified locally, and
                // every semantic warning is acknowledged, before the batch's first Graph call. A single
                // entry's failure or an unacknowledged warning must not let any other entry's Graph
                // write through, so this always runs over the whole batch up front rather than per entry.
                var gateResult = await RunPreflightAsync(
                    entries,
                    packageDirectory,
                    repoRoot,
                    services.GetRequiredService<IPkgBundleInspector>(),
                    services.GetRequiredService<ILoggerFactory>().CreateLogger<PublishPreflight>(),
                    CliVersion.Current,
                    force,
                    () => SemanticWarningGate.IsInteractive(() => Console.IsInputRedirected, () => Console.IsOutputRedirected),
                    () => SemanticWarningGate.ConfirmOnConsole(Console.In, Console.Out),
                    Console.Write,
                    Console.Error.WriteLine,
                    cancellationToken);

                if (gateResult.AbortExitCode is { } abortExitCode)
                {
                    await WriteResultFileAsync(resultFile, gateResult.AbortResultEntries ?? [], cancellationToken);
                    return abortExitCode;
                }

                using var composition = PublishComposition.Create(
                    graphOptions, services.GetRequiredService<ILoggerFactory>());

                return await PublishEntriesAsync(
                    composition.Orchestrator, gateResult.Entries, repoRoot, packageDirectory,
                    sourceCommit, allowDowngrade, dryRun, resultFile, cancellationToken,
                    composition.VerifyTenantAsync, gateResult.WarningsAcknowledgedViaForce);
            }
            catch (PublisherException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    /// <param name="AbortExitCode">Set when the batch must not proceed to Graph at all; null on success.</param>
    /// <param name="AbortResultEntries">The result-file entries to write when aborted; null on success.</param>
    /// <param name="Entries">
    /// On success, every input entry with <see cref="PublishEntry.VerifiedArtifacts"/> and
    /// <see cref="PublishEntry.Warnings"/> attached. Empty when aborted - the caller must not iterate
    /// it, but an empty list is also itself the proof that nothing downstream can reach Graph.
    /// </param>
    internal sealed record PreflightGateResult(
        int? AbortExitCode,
        List<PublishResultEntry>? AbortResultEntries,
        List<PublishEntry> Entries,
        bool? WarningsAcknowledgedViaForce);

    /// <summary>
    /// Runs the all-entry, zero-Graph-write preflight and semantic-warning gate (issue #116,
    /// doc/00-overview.md 6.21) ahead of the publish loop. Every entry is checked locally - artifact
    /// existence/hash/re-inspection for macOS, existence/identity for every other platform - and every
    /// semantic warning across the whole batch is acknowledged (via a single TTY confirmation or
    /// <paramref name="force"/>) before any entry is handed to <see cref="PublishEntriesAsync"/>. A
    /// failure in either step aborts with an empty <see cref="PreflightGateResult.Entries"/>, so the
    /// caller's next step (constructing a Graph client and looping) is unreachable.
    /// </summary>
    internal static async Task<PreflightGateResult> RunPreflightAsync(
        List<PublishEntry> entries,
        string packageDirectory,
        string repoRoot,
        IPkgBundleInspector inspector,
        ILogger<PublishPreflight> preflightLogger,
        string cliVersion,
        bool force,
        Func<bool> isInteractive,
        Func<bool> confirm,
        Action<string> writeLine,
        Action<string> writeErrorLine,
        CancellationToken cancellationToken)
    {
        var preflightItems = entries.Select(entry => new PreflightItem(
                entry.Loaded.Manifest,
                entry.App,
                new AppIdentity(entry.Loaded.Manifest.PackageIdentifier!, entry.App.Platform!, AppArchitecture.Resolve(entry.App)!),
                $"{entry.Loaded.Manifest.PackageIdentifier} {entry.App.Platform}-{AppArchitecture.Resolve(entry.App)}",
                entry.Loaded.Path))
            .ToList();
        var preflight = new PublishPreflight(inspector, preflightLogger);
        var preflightResult = await preflight.RunAsync(preflightItems, packageDirectory, cliVersion, cancellationToken);

        if (preflightResult.Failures.Count > 0)
        {
            foreach (var failure in preflightResult.Failures)
            {
                writeErrorLine($"error: {failure.Item.EntryLabel}: {failure.Message}");
            }

            return new PreflightGateResult(
                ExitCodes.Failure,
                [.. preflightResult.Failures.Select(f => PreflightFailureResult(f, repoRoot))],
                [],
                null);
        }

        var warnedEntries = preflightResult.Entries.Where(e => e.Warnings.Count > 0).ToList();
        // Tri-state: null (nothing to acknowledge), false (interactive [y/N] accept), true (--force).
        // Recorded per warned entry in the result file so a CI log can distinguish an operator's
        // interactive accept from an unattended --force run.
        bool? warningsAcknowledgedViaForce = null;
        if (warnedEntries.Count > 0)
        {
            writeLine(PkgInspectionWarningFormatter.FormatBatch(
                [.. warnedEntries.Select(e => (e.Item.EntryLabel, e.Warnings))]));

            var decision = SemanticWarningGate.Decide(hasWarnings: true, force, isInteractive(), confirm);
            switch (decision)
            {
                case WarningGateDecision.ForceAcknowledged:
                    warningsAcknowledgedViaForce = true;
                    writeLine("Semantic PKG inspection warnings acknowledged via --force.\n");
                    break;
                case WarningGateDecision.Acknowledged:
                    warningsAcknowledgedViaForce = false;
                    writeLine("Semantic PKG inspection warnings acknowledged.\n");
                    break;
                case WarningGateDecision.ForceRequired:
                case WarningGateDecision.Declined:
                    writeErrorLine(decision == WarningGateDecision.ForceRequired
                        ? "error: semantic PKG inspection warnings were found in a non-interactive run. Re-run with --force to acknowledge them."
                        : "error: semantic PKG inspection warnings were not acknowledged.");
                    return new PreflightGateResult(
                        ExitCodes.Failure,
                        [.. warnedEntries.Select(e => PreflightWarningDeclinedResult(e, repoRoot))],
                        [],
                        null);
            }
        }

        // Every entry's artifact is already verified; attach it (and its warnings, for result output)
        // so the orchestrator does not read - and re-trust - package-metadata.json a second time.
        var verifiedEntries = entries.Zip(preflightResult.Entries, (entry, preflighted) =>
                entry with { VerifiedArtifacts = preflighted.Artifacts, Warnings = preflighted.Warnings })
            .ToList();
        return new PreflightGateResult(null, null, verifiedEntries, warningsAcknowledgedViaForce);
    }

    private static PublishResultEntry PreflightFailureResult(PreflightFailure failure, string repoRoot)
        => new(
            failure.Item.Manifest.PackageIdentifier ?? "",
            failure.Item.Manifest.PackageVersion ?? "",
            failure.Item.App.Platform ?? "",
            AppArchitecture.Resolve(failure.Item.App) ?? "",
            GetBestEffortManifestRepoRelativePath(repoRoot, failure.Item.ManifestPath),
            "failed",
            null,
            null,
            failure.Message);

    private static PublishResultEntry PreflightWarningDeclinedResult(PreflightEntry entry, string repoRoot)
        => new(
            entry.Item.Manifest.PackageIdentifier ?? "",
            entry.Item.Manifest.PackageVersion ?? "",
            entry.Item.App.Platform ?? "",
            AppArchitecture.Resolve(entry.Item.App) ?? "",
            GetBestEffortManifestRepoRelativePath(repoRoot, entry.Item.ManifestPath),
            "failed",
            null,
            null,
            "Semantic PKG inspection warnings were not acknowledged.",
            CategoryOutcome: null,
            WarningCodes: [.. entry.Warnings.Select(w => w.Code.ToString())],
            // null, not false: false means an interactive [y/N] accepted the warnings (see
            // PublishResultEntry.ForceAcknowledged), which did not happen on this aborted path.
            ForceAcknowledged: null);

    /// <param name="VerifiedArtifacts">Set by preflight (issue #116) once every entry has been re-verified; null before that point.</param>
    /// <param name="Warnings">This entry's macOS semantic PKG inspection warnings, set by preflight; empty for every other platform or before preflight runs.</param>
    internal sealed record PublishEntry(
        IntuneLobPublisher.Core.Validation.LoadedManifest Loaded,
        AppManifest App,
        PackageArtifacts? VerifiedArtifacts = null,
        IReadOnlyList<PkgInspectionWarning>? Warnings = null);

    private readonly record struct MacOsMigrationTarget(
        string PackageIdentifier,
        string Platform,
        string DisplayName);

    /// <summary>
    /// When several manifests target the same app identity, only the highest PackageVersion is
    /// published. macOS universal migration aliases that share the DisplayName fallback target are
    /// then collapsed by version too (doc/00-overview.md 6.8); the rest are reported as skipped.
    /// </summary>
    internal static List<PublishEntry> SelectHighestVersions(
        IReadOnlyList<IntuneLobPublisher.Core.Validation.LoadedManifest> manifests)
    {
        var byIdentity = new Dictionary<AppIdentity, PublishEntry>();
        foreach (var loaded in manifests)
        {
            foreach (var app in loaded.Manifest.Apps)
            {
                var identity = new AppIdentity(
                    loaded.Manifest.PackageIdentifier!, app.Platform!, AppArchitecture.Resolve(app)!);
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

        return CollapseMacOsArchitectureMigrations([.. byIdentity.Values]);
    }

    /// <summary>
    /// Collapses historical explicit-architecture entries with their macOS universal migration target
    /// before preflight or Graph access. DisplayName is the resolver's fallback key, so publishing both
    /// entries could otherwise update the same Intune app in input order and leave historical content active.
    /// </summary>
    private static List<PublishEntry> CollapseMacOsArchitectureMigrations(List<PublishEntry> entries)
    {
        var migrationGroups = entries
            .Where(entry => string.Equals(entry.App.Platform, "macos", StringComparison.Ordinal))
            .GroupBy(entry => new MacOsMigrationTarget(
                entry.Loaded.Manifest.PackageIdentifier!, entry.App.Platform!, entry.App.DisplayName!))
            .Where(group =>
            {
                var architectures = group
                    .Select(entry => AppArchitecture.Resolve(entry.App)!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return architectures.Count > 1
                    && architectures.Contains(AppArchitecture.MacOsDefault, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();

        if (migrationGroups.Count == 0)
        {
            return entries;
        }

        var winnerByTarget = new Dictionary<MacOsMigrationTarget, PublishEntry>();
        foreach (var group in migrationGroups)
        {
            var winner = group.First();
            foreach (var candidate in group.Skip(1))
            {
                var comparison = PublishGuard.CompareVersions(
                    candidate.Loaded.Manifest.PackageVersion!, winner.Loaded.Manifest.PackageVersion!);
                if (comparison > 0 || (comparison == 0 && IsUniversal(candidate) && !IsUniversal(winner)))
                {
                    winner = candidate;
                }
            }

            winnerByTarget[group.Key] = winner;
            foreach (var loser in group.Where(entry => !ReferenceEquals(entry, winner)))
            {
                Console.WriteLine(
                    $"Skipping {group.Key.PackageIdentifier} {group.Key.Platform}-{AppArchitecture.Resolve(loser.App)} " +
                    $"version {loser.Loaded.Manifest.PackageVersion} from '{loser.Loaded.Path}' " +
                    $"(collapsed into macOS universal migration target {group.Key.Platform}-{AppArchitecture.Resolve(winner.App)} " +
                    $"version {winner.Loaded.Manifest.PackageVersion} from '{winner.Loaded.Path}' for DisplayName '{group.Key.DisplayName}').");
            }
        }

        var emittedTargets = new HashSet<MacOsMigrationTarget>();
        var collapsed = new List<PublishEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var target = new MacOsMigrationTarget(
                entry.Loaded.Manifest.PackageIdentifier!, entry.App.Platform!, entry.App.DisplayName!);
            if (!winnerByTarget.TryGetValue(target, out var winner))
            {
                collapsed.Add(entry);
                continue;
            }

            if (emittedTargets.Add(target))
            {
                collapsed.Add(winner);
            }
        }

        return collapsed;
    }

    private static bool IsUniversal(PublishEntry entry)
        => string.Equals(
            AppArchitecture.Resolve(entry.App), AppArchitecture.MacOsDefault, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Publishes entries one by one, continuing on per-app failures so one broken app does not block
    /// the rest of a CI batch (reruns converge, doc/00-overview.md 6.10). Tenant mismatch,
    /// authentication failures and identity-wide Graph authorization failures abort the whole run —
    /// nothing else can succeed after those, so continuing would only repeat the same error per entry.
    /// </summary>
    internal static async Task<int> PublishEntriesAsync(
        IPublishOrchestrator orchestrator,
        List<PublishEntry> entries,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun,
        string? resultFile,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? verifyTenant = null,
        bool? warningsAcknowledgedViaForce = null)
    {
        // Tenant verification is an explicit preflight step (issue #116): it must happen after every
        // entry's local checks pass and before the loop's first Graph call, not as an incidental side
        // effect of whichever request the orchestrator happens to issue first.
        if (verifyTenant is not null)
        {
            try
            {
                await verifyTenant(cancellationToken).ConfigureAwait(false);
            }
            catch (TenantMismatchException ex)
            {
                await WriteResultFileAsync(resultFile, [], cancellationToken);
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (GraphAccessDeniedException ex)
            {
                await WriteResultFileAsync(resultFile, [], cancellationToken);
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                var message = $"Graph authentication failed: {ex.Message}";
                await WriteResultFileAsync(resultFile, [], cancellationToken);
                Console.Error.WriteLine($"error: {message}");
                return ExitCodes.Failure;
            }
        }

        var published = 0;
        var skippedDowngrade = 0;
        var skippedPlatform = 0;
        var failed = 0;
        var resultEntries = new List<PublishResultEntry>();

        foreach (var entry in entries)
        {
            var label = $"{entry.Loaded.Manifest.PackageIdentifier} {entry.App.Platform}-{AppArchitecture.Resolve(entry.App)}";
            var warningCodes = entry.Warnings is { Count: > 0 }
                ? entry.Warnings.Select(w => w.Code.ToString()).ToArray()
                : null;
            var forceAcknowledgedForEntry = warningCodes is null ? (bool?)null : warningsAcknowledgedViaForce;

            try
            {
                var request = CreatePublishRequest(
                    entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun);
                var result = await orchestrator.PublishAsync(
                    request,
                    new PublishReport
                    {
                        ReportCategoryPlan = plan => Console.Write(CategoryPlanFormatter.Format(plan)),
                        ReportAssignmentPlan = plan => Console.Write(AssignmentPlanFormatter.Format(plan)),
                    },
                    cancellationToken);
                resultEntries.Add(PublishResultOutput.FromResult(request, result, warningCodes, forceAcknowledgedForEntry));

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
                AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message, warningCodes, forceAcknowledgedForEntry);
                await WriteResultFileAsync(resultFile, resultEntries, cancellationToken);
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (GraphAccessDeniedException ex)
            {
                AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message, warningCodes, forceAcknowledgedForEntry);
                await WriteResultFileAsync(resultFile, resultEntries, cancellationToken);
                Console.Error.WriteLine($"error: {label}: {ex.Message}");
                return ExitCodes.Failure;
            }
            catch (Azure.Identity.AuthenticationFailedException ex)
            {
                var message = $"Graph authentication failed: {ex.Message}";
                AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, message, warningCodes, forceAcknowledgedForEntry);
                await WriteResultFileAsync(resultFile, resultEntries, cancellationToken);
                Console.Error.WriteLine($"error: {message}");
                return ExitCodes.Failure;
            }
            catch (PublisherException ex)
            {
                failed++;
                AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message, warningCodes, forceAcknowledgedForEntry);
                Console.Error.WriteLine($"error: {label}: {ex.Message}");
            }
        }

        await WriteResultFileAsync(resultFile, resultEntries, cancellationToken);
        Console.WriteLine(
            $"{published} published, {skippedDowngrade} skipped (downgrade), " +
            $"{skippedPlatform} skipped (platform), {failed} failed.");
        return failed == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static void AddFailureResult(
        List<PublishResultEntry> resultEntries,
        PublishEntry entry,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun,
        string message,
        string[]? warningCodes = null,
        bool? forceAcknowledged = null)
    {
        try
        {
            var request = CreatePublishRequest(
                entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun);
            resultEntries.Add(PublishResultOutput.FromFailure(request, message, warningCodes, forceAcknowledged));
        }
        catch (PublisherException ex)
        {
            resultEntries.Add(new PublishResultEntry(
                entry.Loaded.Manifest.PackageIdentifier ?? "",
                entry.Loaded.Manifest.PackageVersion ?? "",
                entry.App.Platform ?? "",
                AppArchitecture.Resolve(entry.App) ?? "",
                GetBestEffortManifestRepoRelativePath(repoRoot, entry.Loaded.Path),
                "failed",
                null,
                null,
                $"{message}; additionally failed to resolve manifest path: {ex.Message}",
                CategoryOutcome: null,
                WarningCodes: warningCodes,
                ForceAcknowledged: forceAcknowledged));
        }
    }

    private static PublishRequest CreatePublishRequest(
        PublishEntry entry,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun)
        => new(
            entry.Loaded.Manifest,
            entry.App,
            GetManifestRepoRelativePath(repoRoot, entry.Loaded.Path),
            repoRoot,
            packageDirectory,
            sourceCommit,
            allowDowngrade,
            dryRun,
            entry.VerifiedArtifacts);

    internal static Task WriteResultFileAsync(
        string? resultFile,
        IReadOnlyList<PublishResultEntry> resultEntries,
        CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(resultFile)
            ? Task.CompletedTask
            : PublishResultOutput.WriteAsync(resultFile, resultEntries, cancellationToken);

    private static void ValidateResultFileDirectory(string? resultFile)
    {
        if (string.IsNullOrWhiteSpace(resultFile))
        {
            return;
        }

        PublishResultOutput.GetValidatedFullPath(resultFile);
    }

    private static string GetManifestRepoRelativePath(string repoRoot, string manifestPath)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(repoRoot),
            Path.GetFullPath(manifestPath));
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ManifestLoadException(
                $"Manifest '{manifestPath}' must be under --repo-root '{repoRoot}' to record repository-relative metadata.");
        }

        return relativePath.Replace('\\', '/');
    }

    private static string GetBestEffortManifestRepoRelativePath(string repoRoot, string manifestPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(
                    Path.GetFullPath(repoRoot),
                    Path.GetFullPath(manifestPath));
            if (!Path.IsPathRooted(relativePath))
            {
                return relativePath.Replace('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        try
        {
            var fileName = Path.GetFileName(manifestPath);
            return string.IsNullOrWhiteSpace(fileName) ? "unknown" : fileName;
        }
        catch (ArgumentException)
        {
            return "unknown";
        }
    }
}
