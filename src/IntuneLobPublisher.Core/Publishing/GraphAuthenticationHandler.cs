using System.Net.Http.Headers;
using Azure.Core;
using IntuneLobPublisher.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Attaches a Graph bearer token acquired via the configured <see cref="TokenCredential"/> to every
/// request. Verifies the token's `tid` claim against <see cref="GraphClientOptions.ExpectedTenantId"/>
/// on every fresh token acquisition, so a tenant change is caught before the next write. Also logs the
/// token's non-secret identity claims (`appid`/`idtyp`/`roles`) on every fresh acquisition, independent
/// of whether a tenant is configured, so a `DefaultAzureCredential` chain that silently resolved to the
/// wrong identity is visible in the log rather than only inferable from a 403 (doc/00-overview.md 6.19).
/// </summary>
public sealed class GraphAuthenticationHandler : DelegatingHandler
{
    private readonly TokenCredential _credential;
    private readonly GraphClientOptions _options;
    private readonly ILogger<GraphAuthenticationHandler> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    // Refresh a little before actual expiry so a request never starts with a token that expires mid-flight.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private AccessToken? _cachedToken;

    public GraphAuthenticationHandler(TokenCredential credential, GraphClientOptions options, ILogger<GraphAuthenticationHandler> logger)
    {
        _credential = credential;
        _options = options;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureRequestTargetsGraph(request);
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires (or reuses the cached) Graph token and verifies its tenant, without issuing any HTTP
    /// request. Publish preflight calls this explicitly so tenant verification is a deliberate step
    /// before the batch's first Graph write, rather than an incidental side effect of whichever request
    /// happens to run first (doc/00-overview.md 6.21).
    /// </summary>
    public Task EnsureTenantVerifiedAsync(CancellationToken cancellationToken)
        => GetTokenAsync(cancellationToken);

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

            // Logged before VerifyTenant, and independent of it: VerifyTenant returns immediately when
            // ExpectedTenantId is not configured, but the identity matters most exactly when no tenant
            // guard is in place. Logging first also means the identity is on record even when
            // VerifyTenant throws below.
            LogTokenIdentity(token.Token);
            VerifyTenant(token.Token);
            _cachedToken = token;
            return token.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // appid (a GUID), idtyp and roles (permission names) are not secrets - the same class of value as
    // the client-request-id/request-id correlation ids GraphErrorReader logs. The access token itself
    // is never passed to the logger.
    private void LogTokenIdentity(string accessToken)
    {
        var identity = JwtTenantIdReader.ReadIdentity(accessToken);
        _logger.LogInformation(
            "Acquired Graph token for identity appid={AppId} idtyp={IdentityType} roles={Roles}.",
            identity.AppId ?? "(none)",
            identity.IdentityType ?? "(none)",
            identity.Roles.Count == 0 ? "(none)" : string.Join(", ", identity.Roles));
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
