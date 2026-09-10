using Azure.Core;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Publishing.Categories;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>
/// A Graph session scoped to one manifest entry (issue #150): the <see cref="HttpClient"/>, its
/// authentication/retry handlers and the services built on it. <see cref="PublishCommand"/> creates one
/// per entry and disposes it before moving to the next, so a connection or cached token never carries
/// from one package's publish to the next. The <see cref="TokenCredential"/> itself is deliberately
/// *not* part of the session - see <see cref="PublishComposition.Create"/>.
/// </summary>
internal interface IPublishSession : IDisposable
{
    IPublishOrchestrator Orchestrator { get; }
}

/// <summary>
/// Builds the Graph pipeline and publish services for one manifest entry (issue #150):
/// <see cref="GraphClientOptions"/> depends on <c>--expected-tenant</c>, so this cannot live in the
/// root service provider. One <see cref="HttpClient"/> serves every Graph client for this entry. Its
/// <c>/v1.0/</c> base address only matters for calls that build a relative request path; every client
/// that needs to reach <c>/beta/</c> (app resolution, Windows <c>win32LobApp</c>, macOS
/// <c>AppType: pkg</c>, filter-bearing assignments) builds an absolute path instead, replacing the
/// base path segment correctly (<see cref="GraphWin32LobAppClient"/>,
/// <see cref="AssignmentGraphClient"/>, <see cref="GraphIntuneAppDirectory"/>,
/// <see cref="GraphMacOsAppClient"/>, <see cref="GraphMobileAppContentClient"/>,
/// <see cref="CategoryGraphClient"/>). <see cref="CategoryGraphClient"/> additionally reads the base
/// address to build the <c>@odata.id</c> of a category reference from its scheme and authority.
/// </summary>
internal sealed class PublishComposition : IPublishSession
{
    private readonly HttpClient _graphHttpClient;

    private PublishComposition(HttpClient graphHttpClient, IPublishOrchestrator orchestrator)
    {
        _graphHttpClient = graphHttpClient;
        Orchestrator = orchestrator;
    }

    public IPublishOrchestrator Orchestrator { get; }

    /// <summary>
    /// Builds a fresh session on <paramref name="credential"/>. The credential is shared across every
    /// entry in the run rather than created per session: <see cref="Azure.Identity.DefaultAzureCredential"/>
    /// re-resolves its chain per instance, and with <c>AZURE_TOKEN_CREDENTIALS</c> unset (the situation
    /// <see cref="CredentialDeterminismCheck"/> warns about) a per-entry instance could silently resolve
    /// to a different identity mid-run - inside the tenant <c>--expected-tenant</c> checks, so it would
    /// not be caught. <see cref="GraphAuthenticationHandler"/>'s token cache is still per-session, so
    /// each entry still re-verifies the tenant and re-logs the token identity (doc/00-overview.md 6.12).
    /// </summary>
    public static PublishComposition Create(TokenCredential credential, GraphClientOptions options, ILoggerFactory loggerFactory)
    {
        var httpClient = GraphClientFactory.Create(credential, options, loggerFactory);

        var windowsPublisher = new WindowsAppPublisher(
            new GraphWin32LobAppClient(httpClient),
            new MobileAppContentUploadOrchestrator(
                new GraphMobileAppContentClient(httpClient),
                new AzureStorageBlockBlobUploader(logger: loggerFactory.CreateLogger<AzureStorageBlockBlobUploader>())),
            new IntuneWinContentExtractor());

        var macOsPublisher = new MacOsAppPublisher(
            new GraphMacOsAppClient(httpClient),
            new MobileAppContentUploadOrchestrator(
                new GraphMobileAppContentClient(httpClient),
                new AzureStorageBlockBlobUploader(logger: loggerFactory.CreateLogger<AzureStorageBlockBlobUploader>())),
            new PkgContentPreparer(),
            loggerFactory.CreateLogger<MacOsAppPublisher>());

        var platformPublishers = new Dictionary<string, IPlatformAppPublisher>(StringComparer.Ordinal)
        {
            ["windows"] = windowsPublisher,
            ["macos"] = macOsPublisher,
        };

        var orchestrator = new PublishOrchestrator(
            new IntuneAppResolver(new GraphIntuneAppDirectory(httpClient)),
            platformPublishers,
            new CategoryService(
                new CategoryGraphClient(httpClient),
                loggerFactory.CreateLogger<CategoryService>()),
            new AssignmentService(
                new AssignmentGraphClient(httpClient),
                loggerFactory.CreateLogger<AssignmentService>()),
            loggerFactory.CreateLogger<PublishOrchestrator>());
        return new PublishComposition(httpClient, orchestrator);
    }

    public void Dispose() => _graphHttpClient.Dispose();
}
