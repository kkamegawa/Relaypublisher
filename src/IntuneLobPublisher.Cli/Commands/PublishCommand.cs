using System.CommandLine;
using Azure.Identity;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
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

                var loggerFactory = services.GetRequiredService<ILoggerFactory>();

                // Warned once for the whole run, not per entry (doc/00-overview.md 6.19): the logger
                // category is intentionally still `PublishComposition`, matching every session's own Graph
                // logging, rather than `PublishCommand`, so this line does not change under issue #150's
                // per-entry session split.
                CredentialDeterminismCheck.WarnIfCredentialChainNotPinned(
                    loggerFactory.CreateLogger<PublishComposition>(), Environment.GetEnvironmentVariable);

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

                // Shared for the whole run (see PublishComposition.Create); only the HttpClient-based
                // session below is rebuilt per manifest entry (issue #150).
                var credential = new DefaultAzureCredential();
                IPublishSession CreateSession() => PublishComposition.Create(credential, graphOptions, loggerFactory);

                return await PublishEntriesAsync(
                    CreateSession, entries, repoRoot, packageDirectory,
                    sourceCommit, allowDowngrade, dryRun, resultFile, cancellationToken);
            }
            catch (PublisherException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    internal sealed record PublishEntry(IntuneLobPublisher.Core.Validation.LoadedManifest Loaded, AppManifest App);

    /// <summary>
    /// When several manifests target the same app identity, only the highest PackageVersion is
    /// published (doc/00-overview.md 6.8); the rest are reported as skipped.
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
    /// the rest of a CI batch (reruns converge, doc/00-overview.md 6.10). Tenant mismatch,
    /// authentication failures, identity-wide Graph authorization failures, and any exception type this
    /// loop does not otherwise recognize abort the whole run — nothing else can safely continue after
    /// those. <paramref name="createSession"/> is called once per entry and each session is disposed
    /// before the next entry starts (issue #150): the Graph <see cref="HttpClient"/>, its handlers and
    /// their cached token are never reused across manifest entries. The result file has a single write
    /// point at the end (in a <c>finally</c>, using <see cref="CancellationToken.None"/>) so it is
    /// produced whichever way the loop above ends, instead of one write per abort path.
    /// </summary>
    internal static async Task<int> PublishEntriesAsync(
        Func<IPublishSession> createSession,
        List<PublishEntry> entries,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun,
        string? resultFile,
        CancellationToken cancellationToken)
    {
        var published = 0;
        var skippedDowngrade = 0;
        var skippedPlatform = 0;
        var failed = 0;
        var resultEntries = new List<PublishResultEntry>();
        var aborted = false;
        var resultFileWriteFailed = false;

        try
        {
            foreach (var entry in entries)
            {
                var label = $"{entry.Loaded.Manifest.PackageIdentifier} {entry.App.Platform}-{entry.App.Architecture}";

                try
                {
                    using var session = createSession();
                    var request = CreatePublishRequest(
                        entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun);
                    var result = await session.Orchestrator.PublishAsync(
                        request,
                        new PublishReport
                        {
                            ReportCategoryPlan = plan => Console.Write(CategoryPlanFormatter.Format(plan)),
                            ReportAssignmentPlan = plan => Console.Write(AssignmentPlanFormatter.Format(plan)),
                        },
                        cancellationToken);
                    resultEntries.Add(PublishResultOutput.FromResult(request, result));

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
                    AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message);
                    Console.Error.WriteLine($"error: {ex.Message}");
                    aborted = true;
                    break;
                }
                catch (GraphAccessDeniedException ex)
                {
                    AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message);
                    Console.Error.WriteLine($"error: {label}: {ex.Message}");
                    aborted = true;
                    break;
                }
                catch (Azure.Identity.AuthenticationFailedException ex)
                {
                    var message = $"Graph authentication failed: {ex.Message}";
                    AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, message);
                    Console.Error.WriteLine($"error: {message}");
                    aborted = true;
                    break;
                }
                catch (PublisherException ex)
                {
                    failed++;
                    AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, ex.Message);
                    Console.Error.WriteLine($"error: {label}: {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Anything else is a failure mode this loop was not written to reason about safely, so
                    // it stops the batch rather than guessing it is a per-entry problem (issue #150: this
                    // used to escape uncaught entirely - e.g. Azure.RequestFailedException from a blob
                    // upload - killing the process before the result file below was ever written). A
                    // genuine OperationCanceledException from `cancellationToken` is deliberately excluded
                    // so caller cancellation still propagates as a cancellation, not a recorded failure.
                    //
                    // Record only the type name, never ex.Message: every PublisherException subtype's
                    // message is deliberately reviewed to carry no secrets (e.g. ContentUploadRejectedException
                    // never keeps a signed URL - see AGENTS.md and PublicHttpSourceProvider.RedactQuery for the
                    // same principle applied to URLs), but this catch-all sees exception types this codebase
                    // has not vetted, and an arbitrary Message could carry a token or signed URL (Copilot
                    // review, PR #151).
                    var message = ex.GetType().Name;
                    AddFailureResult(resultEntries, entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun, message);
                    Console.Error.WriteLine($"error: {label}: {message}");
                    aborted = true;
                    break;
                }
            }
        }
        finally
        {
            // Single exit point (issue #150): reached whether the loop finished, aborted, or an
            // unexpected exception was recorded above. CancellationToken.None so a cancelled run still
            // gets to persist whatever it completed; a write failure here is logged, never left to mask
            // whatever publish failure is already propagating, but still makes a completed run fail.
            try
            {
                await WriteResultFileAsync(resultFile, resultEntries, CancellationToken.None);
            }
            catch (Exception ex)
            {
                resultFileWriteFailed = true;
                Console.Error.WriteLine($"error: failed to write --result-file: {ex.Message}");
            }
        }

        if (aborted)
        {
            return ExitCodes.Failure;
        }

        Console.WriteLine(
            $"{published} published, {skippedDowngrade} skipped (downgrade), " +
            $"{skippedPlatform} skipped (platform), {failed} failed.");
        return failed == 0 && !resultFileWriteFailed ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static void AddFailureResult(
        List<PublishResultEntry> resultEntries,
        PublishEntry entry,
        string repoRoot,
        string packageDirectory,
        string sourceCommit,
        bool allowDowngrade,
        bool dryRun,
        string message)
    {
        try
        {
            var request = CreatePublishRequest(
                entry, repoRoot, packageDirectory, sourceCommit, allowDowngrade, dryRun);
            resultEntries.Add(PublishResultOutput.FromFailure(request, message));
        }
        catch (PublisherException ex)
        {
            resultEntries.Add(new PublishResultEntry(
                entry.Loaded.Manifest.PackageIdentifier ?? "",
                entry.Loaded.Manifest.PackageVersion ?? "",
                entry.App.Platform ?? "",
                entry.App.Architecture ?? "",
                GetBestEffortManifestRepoRelativePath(repoRoot, entry.Loaded.Path),
                "failed",
                null,
                null,
                $"{message}; additionally failed to resolve manifest path: {ex.Message}"));
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
            dryRun);

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
