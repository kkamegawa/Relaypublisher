using System.CommandLine;
using Azure.Identity;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// `publish` currently covers assignment sync (issue-004): it resolves each manifest app in
/// Intune, prints the assignment plan diff and, unless <c>--dry-run</c> is set, applies it.
/// App create/update and content upload wiring is tracked by issue-003.
/// </summary>
internal static class PublishCommand
{
    public static Command Create(IServiceProvider services)
    {
        var manifestOption = CommandSupport.ManifestOption();
        var manifestListOption = CommandSupport.ManifestListOption();
        var repoRootOption = CommandSupport.RepoRootOption();
        var verboseOption = CommandSupport.VerboseOption();

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Prints the assignment plan diff without changing anything in Intune.",
        };

        var expectedTenantOption = new Option<string?>("--expected-tenant")
        {
            Description = "Entra ID tenant id the Graph token must belong to; a mismatch fails before any write.",
        };

        var command = new Command("publish", "Applies manifest assignments to Microsoft Intune apps (app create/update flow is tracked by issue-003).");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dryRunOption);
        command.Options.Add(expectedTenantOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var repoRoot = parseResult.GetValue(repoRootOption)!;
                var dryRun = parseResult.GetValue(dryRunOption);
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

                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var options = new GraphClientOptions
                {
                    ExpectedTenantId = parseResult.GetValue(expectedTenantOption),
                };
                using var graphClient = GraphClientFactory.Create(new DefaultAzureCredential(), options, loggerFactory);

                var resolver = new IntuneAppResolver(new GraphIntuneAppDirectory(graphClient));
                IAssignmentService assignmentService = new AssignmentService(
                    new GraphAppAssignmentClient(graphClient),
                    loggerFactory.CreateLogger<AssignmentService>());

                return await SyncAssignmentsAsync(manifests, resolver, assignmentService, dryRun, cancellationToken);
            }
            catch (PublisherException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitCodes.Failure;
            }
        });

        return command;
    }

    private static async Task<int> SyncAssignmentsAsync(
        IReadOnlyList<LoadedManifest> manifests,
        IntuneAppResolver resolver,
        IAssignmentService assignmentService,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var failures = 0;

        foreach (var loaded in manifests)
        {
            var syncMode = AssignmentSyncModes.Parse(loaded.Manifest.AssignmentSync);

            foreach (var app in loaded.Manifest.Apps)
            {
                var identity = new AppIdentity(loaded.Manifest.PackageIdentifier!, app.Platform!, app.Architecture!);
                var resolution = await resolver.ResolveAsync(identity, app.DisplayName!, cancellationToken);
                if (resolution.Outcome == AppResolutionOutcome.NotFound)
                {
                    Console.Error.WriteLine(
                        $"error: no Intune app found for '{identity.PackageIdentifier}' ({identity.Platform}/{identity.Architecture}). " +
                        "The publish app create/update flow is not wired yet (issue-003); create the app first.");
                    failures++;
                    continue;
                }

                var plan = await assignmentService.CreatePlanAsync(resolution.AppId!, app, syncMode, cancellationToken);
                Console.Write(AssignmentPlanFormatter.Format(plan));

                if (dryRun)
                {
                    continue;
                }

                await assignmentService.ApplyAsync(plan, app, cancellationToken);
            }
        }

        if (dryRun)
        {
            Console.WriteLine("Dry run: no changes were applied.");
        }

        return failures == 0 ? ExitCodes.Success : ExitCodes.Failure;
    }
}
