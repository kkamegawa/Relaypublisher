using System.Net.Http.Headers;
using Azure.Core;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Attaches a Graph bearer token acquired via the configured <see cref="TokenCredential"/> to every
/// request. Verifies the token's `tid` claim against <see cref="GraphClientOptions.ExpectedTenantId"/>
/// on every fresh token acquisition, so a tenant change is caught before the next write.
/// </summary>
public sealed class GraphAuthenticationHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly GraphClientOptions _options;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Refresh a little before actual expiry so a request never starts with a token that expires mid-flight.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private AccessToken? _cachedToken;

    public GraphAuthenticationHandler(TokenCredential credential, GraphClientOptions options)
    {
        _credential = credential;
        _options = options;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureRequestTargetsGraph(request);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Guards against attaching the Graph bearer token to a request that (by caller mistake) targets
    // some other host, which would leak the token to an unintended endpoint.
    private void EnsureRequestTargetsGraph(HttpRequestMessage request)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            return;
        }

        if (!string.Equals(uri.Scheme, _options.BaseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _options.BaseAddress.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to attach a Graph bearer token to a request targeting '{uri.Scheme}://{uri.Host}' " +
                $"(expected '{_options.BaseAddress.Scheme}://{_options.BaseAddress.Host}').");
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow + RefreshSkew)
        {
            return cached.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedToken is { } refreshed && refreshed.ExpiresOn > DateTimeOffset.UtcNow + RefreshSkew)
            {
                return refreshed.Token;
            }

            var context = new TokenRequestContext([_options.Scope]);
            var token = await _credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            VerifyTenant(token.Token);
            _cachedToken = token;
            return token.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void VerifyTenant(string accessToken)
    {
        if (_options.ExpectedTenantId is null)
        {
            return;
        }

        var actualTenantId = JwtTenantIdReader.ReadTenantId(accessToken);
        if (!string.Equals(actualTenantId, _options.ExpectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new TenantMismatchException(_options.ExpectedTenantId, actualTenantId);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tokenLock.Dispose();
        }

        base.Dispose(disposing);
    }
}
