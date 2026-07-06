using Azure.Identity;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// Builds the Graph pipeline and publish services after argument parsing:
/// <see cref="GraphClientOptions"/> depends on <c>--expected-tenant</c>, so this cannot live in the
/// root service provider. One shared <see cref="HttpClient"/> serves every Graph client — the
/// directory/content/app clients use relative paths against the v1.0 base address, and
/// <see cref="AssignmentGraphClient"/>'s absolute <c>/v1.0/</c>-or-<c>/beta/</c> paths replace the
/// base path segment correctly.
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
        var orchestrator = new PublishOrchestrator(
            new IntuneAppResolver(new GraphIntuneAppDirectory(httpClient)),
            new GraphWin32LobAppClient(httpClient),
            new Win32LobAppContentUploadOrchestrator(
                new IntuneWinContentExtractor(),
                new GraphMobileAppContentClient(httpClient),
                new AzureStorageBlockBlobUploader()),
            new AssignmentService(
                new AssignmentGraphClient(httpClient),
                loggerFactory.CreateLogger<AssignmentService>()),
            loggerFactory.CreateLogger<PublishOrchestrator>());
        return new PublishComposition(httpClient, orchestrator);
    }

    public void Dispose() => _graphHttpClient.Dispose();
}
