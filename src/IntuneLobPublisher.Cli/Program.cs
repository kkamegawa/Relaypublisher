using System.CommandLine;
using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Planning;
using IntuneLobPublisher.Core.Sources;
using IntuneLobPublisher.Core.Staging;
using IntuneLobPublisher.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// --verbose is scanned up front so logging is configured before command wiring.
var verbose = args.Contains("--verbose");

var services = new ServiceCollection();
services.AddLogging(builder => builder
    .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    }));
services.AddSingleton<IManifestLoader, ManifestLoader>();
services.AddSingleton<IManifestValidator, ManifestValidator>();
services.AddSingleton<ManifestSetValidator>();
services.AddSingleton<HttpClient>();
services.AddSingleton(new SourceRetryOptions());
services.AddSingleton<DownloadRetryPolicy>();
services.AddSingleton<ISourceProvider, PublicHttpSourceProvider>();
services.AddSingleton<ISourceProvider, GitHubReleaseSourceProvider>();
services.AddSingleton<SourceProviderRegistry>();
services.AddSingleton<IWindowsStagingService, WindowsStagingService>();
services.AddSingleton<IIntuneWinToolDownloader, GitHubIntuneWinToolDownloader>();
services.AddSingleton<IIntuneWinToolResolver, IntuneWinToolResolver>();
services.AddSingleton<IProcessRunner, ProcessRunner>();
services.AddSingleton<IIntuneWinPackager, IntuneWinPackager>();
services.AddSingleton<IGitDiffRunner, GitDiffRunner>();
services.AddSingleton<PlanService>();

await using var serviceProvider = services.BuildServiceProvider();

var rootCommand = new RootCommand("Publishes winget-like YAML manifests as Microsoft Intune LOB apps.");
rootCommand.Subcommands.Add(ValidateCommand.Create(serviceProvider));
rootCommand.Subcommands.Add(PackageCommand.Create(serviceProvider));
rootCommand.Subcommands.Add(PlanCommand.Create(serviceProvider));
rootCommand.Subcommands.Add(PublishCommand.Create(serviceProvider));

return await rootCommand.Parse(args).InvokeAsync();
