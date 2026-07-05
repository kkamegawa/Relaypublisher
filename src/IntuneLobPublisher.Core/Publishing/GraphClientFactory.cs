using Azure.Core;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Builds the Microsoft Graph <see cref="HttpClient"/> pipeline: retry(outer) -&gt; authentication(inner) -&gt; transport.</summary>
public static class GraphClientFactory
{
    public static HttpClient Create(TokenCredential credential, GraphClientOptions options, ILoggerFactory loggerFactory)
    {
        var authHandler = new GraphAuthenticationHandler(credential, options)
        {
            InnerHandler = new SocketsHttpHandler(),
        };

        var retryHandler = new GraphRetryHandler(options, loggerFactory.CreateLogger<GraphRetryHandler>())
        {
            InnerHandler = authHandler,
        };

        return new HttpClient(retryHandler)
        {
            BaseAddress = options.BaseAddress,
        };
    }
}
