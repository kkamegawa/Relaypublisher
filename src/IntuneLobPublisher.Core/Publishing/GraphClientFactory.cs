using Azure.Core;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The built Graph pipeline: the <see cref="HttpClient"/> every Graph client shares, plus the
/// <see cref="GraphAuthenticationHandler"/> so a caller can verify the tenant eagerly (before any
/// request) instead of only as a side effect of the first Graph call.
/// </summary>
public sealed record GraphClientPipeline(HttpClient Client, GraphAuthenticationHandler AuthenticationHandler);

/// <summary>Builds the Microsoft Graph <see cref="HttpClient"/> pipeline: retry(outer) -&gt; authentication(inner) -&gt; transport.</summary>
public static class GraphClientFactory
{
    public static GraphClientPipeline Create(TokenCredential credential, GraphClientOptions options, ILoggerFactory loggerFactory)
    {
        var authHandler = new GraphAuthenticationHandler(credential, options, loggerFactory.CreateLogger<GraphAuthenticationHandler>())
        {
            InnerHandler = new SocketsHttpHandler(),
        };

        var retryHandler = new GraphRetryHandler(options, loggerFactory.CreateLogger<GraphRetryHandler>())
        {
            InnerHandler = authHandler,
        };

        var client = new HttpClient(retryHandler)
        {
            BaseAddress = options.BaseAddress,
        };
        return new GraphClientPipeline(client, authHandler);
    }
}
