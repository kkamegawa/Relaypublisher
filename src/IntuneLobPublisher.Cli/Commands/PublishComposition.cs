using Azure.Identity;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// Builds the Graph pipeline and publish services after argument parsing:
/// <see cref="GraphClientOptions"/> depends on <c>--expected-tenant</c>, so this cannot live in the
/// root service provider. One shared <see cref="HttpClient"/> serves every Graph client. Its
/// <c>/v1.0/</c> base address only matters for calls that build a relative request path; every client
/// that needs to reach <c>/beta/</c> (app resolution, macOS <c>AppType: pkg</c>, filter-bearing
/// assignments) builds an absolute path instead, replacing the base path segment correctly
/// (<see cref="AssignmentGraphClient"/>, <see cref="GraphIntuneAppDirectory"/>,
/// <see cref="GraphMacOsAppClient"/>, <see cref="GraphMobileAppContentClient"/>).
/// </summary>
internal sealed class PublishComposition : IDisposable
{
    private readonly HttpClient _graphHttpClient;

    private PublishComposition(HttpClient graphHttpClient, IPublishOrchestrator orchestrator)
    {
        _graphHttpClient = graphHttpClient;
        Orchestrator = orchestrator;
    }

    public IPublishOrchestrator Orchestrator { get; }

    public static PublishComposition Create(GraphClientOptions options, ILoggerFactory loggerFactory)
    {
        var httpClient = GraphClientFactory.Create(new DefaultAzureCredential(), options, loggerFactory);

        var windowsPublisher = new WindowsAppPublisher(
            new GraphWin32LobAppClient(httpClient),
            new MobileAppContentUploadOrchestrator(
                new GraphMobileAppContentClient(httpClient),
                new AzureStorageBlockBlobUploader()),
            new IntuneWinContentExtractor());

        var macOsPublisher = new MacOsAppPublisher(
            new GraphMacOsAppClient(httpClient),
            new MobileAppContentUploadOrchestrator(
                new GraphMobileAppContentClient(httpClient),
                new AzureStorageBlockBlobUploader()),
            new PkgContentPreparer());

        var platformPublishers = new Dictionary<string, IPlatformAppPublisher>(StringComparer.Ordinal)
        {
            ["windows"] = windowsPublisher,
            ["macos"] = macOsPublisher,
        };

        var orchestrator = new PublishOrchestrator(
            new IntuneAppResolver(new GraphIntuneAppDirectory(httpClient)),
            platformPublishers,
            new AssignmentService(
                new AssignmentGraphClient(httpClient),
                loggerFactory.CreateLogger<AssignmentService>()),
            loggerFactory.CreateLogger<PublishOrchestrator>());
        return new PublishComposition(httpClient, orchestrator);
    }

    public void Dispose() => _graphHttpClient.Dispose();
}
