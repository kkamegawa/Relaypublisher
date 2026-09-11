using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls needed to upload mobile LOB app content: create a content version and file, poll the
/// file's upload state, commit it, patch the app's committed content version and notes, and poll the
/// app's publishing state. See doc/issues/issue-003-intune-graph-win32.md "Content upload flow". Every
/// call is relative to the caller's <see cref="GraphClientOptions.BaseAddress"/> (Graph beta): content
/// sub-resources (contentVersions/files) are inherited from <c>mobileLobApp</c> and generally require an
/// OData type-cast segment after the app id, and every app type this client serves - win32LobApp,
/// macOSPkgApp, macOSLobApp - is on beta (doc/adr/publishing.md 2026-09-10 entry).
/// </summary>
public interface IMobileAppContentClient
{
    /// <summary>Lists all content versions, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<MobileAppContentResponse>> ListContentVersionsAsync(
        string appId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Creates a new content version and returns its id.</summary>
    Task<string> CreateContentVersionAsync(string appId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Lists all files in one content version, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<MobileAppContentFileResponse>> ListContentFilesAsync(
        string appId, string contentVersionId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Creates a content file record and returns its id.</summary>
    Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, CancellationToken cancellationToken);

    /// <summary>Reads the current state of a content file, for polling <c>uploadState</c>.</summary>
    Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Requests a fresh Azure Storage SAS URI before the current one expires.</summary>
    Task RenewUploadAsync(string appId, string contentVersionId, string fileId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Commits an uploaded file with its encryption info.</summary>
    Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, CancellationToken cancellationToken);

    /// <summary>Activates a content version by patching the app's <c>committedContentVersion</c>. Point of no return.</summary>
    Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, CancellationToken cancellationToken);

    /// <summary>Writes management metadata JSON to the app's <c>notes</c> field.</summary>
    Task PatchNotesAsync(string appId, string notes, string oDataType, CancellationToken cancellationToken);

    /// <summary>Reads the app's current <c>publishingState</c> ("notPublished", "processing" or "published").</summary>
    Task<string> GetPublishingStateAsync(string appId, CancellationToken cancellationToken);

    /// <summary>Reads the state needed before deciding whether an interrupted content version can be recovered.</summary>
    Task<MobileAppContentState> GetContentStateAsync(
        string appId, string oDataType, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>, which is expected to be
/// one built by <see cref="GraphClientFactory"/> (authentication + retry already wired). Builds
/// request paths relative to the client's base address (Graph beta).
/// </summary>
public sealed class GraphMobileAppContentClient : IMobileAppContentClient
{
    private readonly HttpClient _httpClient;

    public GraphMobileAppContentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MobileAppContentResponse>> ListContentVersionsAsync(
        string appId, string oDataType, CancellationToken cancellationToken)
    {
        var results = new List<MobileAppContentResponse>();
        string? requestUri = ContentRootPath(appId, oDataType) + "/contentVersions?$select=id";

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            var page = await GraphResponseReader
                .ReadJsonAsync<GraphListPage<MobileAppContentResponse>>(response, requestUri, cancellationToken)
                .ConfigureAwait(false);

            foreach (var contentVersion in page.Value)
            {
                if (string.IsNullOrWhiteSpace(contentVersion.Id))
                {
                    throw GraphResponseReader.BodyFailure(
                        response, $"Graph returned a content version without an id for '{requestUri}'.");
                }

                results.Add(contentVersion);
            }

            requestUri = page.NextLink;
        }

        return results;
    }

    public async Task<string> CreateContentVersionAsync(string appId, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = ContentRootPath(appId, oDataType) + "/contentVersions";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, new MobileAppContentCreateRequest(), cancellationToken)
            .ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppContentResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a content version without an id for '{requestUri}'.");
    }

    public async Task<IReadOnlyList<MobileAppContentFileResponse>> ListContentFilesAsync(
        string appId, string contentVersionId, string oDataType, CancellationToken cancellationToken)
    {
        var results = new List<MobileAppContentFileResponse>();
        string? requestUri = ContentVersionPath(appId, contentVersionId, oDataType)
            + "/files?$select=id,isCommitted,uploadState,name,size,sizeEncrypted";

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            var page = await GraphResponseReader
                .ReadJsonAsync<GraphListPage<MobileAppContentFileResponse>>(response, requestUri, cancellationToken)
                .ConfigureAwait(false);

            foreach (var file in page.Value)
            {
                if (string.IsNullOrWhiteSpace(file.Id))
                {
                    throw GraphResponseReader.BodyFailure(
                        response, $"Graph returned a content file without an id for '{requestUri}'.");
                }

                results.Add(file);
            }

            requestUri = page.NextLink;
        }

        return results;
    }

    public async Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = ContentVersionPath(appId, contentVersionId, oDataType) + "/files";
        var request = new MobileAppContentFileCreateRequest { Name = name, Size = size, SizeEncrypted = sizeEncrypted };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a content file without an id for '{requestUri}'.");
    }

    public async Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await GraphResponseReader.ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenewUploadAsync(string appId, string contentVersionId, string fileId, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType) + "/renewUpload";
        using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType) + "/commit";
        var request = new CommitFileRequest { FileEncryptionInfo = fileEncryptionInfo };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, CommittedContentVersion = contentVersionId };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchNotesAsync(string appId, string notes, string oDataType, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, Notes = notes };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetPublishingStateAsync(string appId, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId) + "?$select=publishingState";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileLobAppContentStateResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.PublishingState;
    }

    public async Task<MobileAppContentState> GetContentStateAsync(
        string appId, string oDataType, CancellationToken cancellationToken)
    {
        _ = ToGraphTypeSegment(oDataType);
        var publishingState = await GetPublishingStateAsync(appId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(publishingState, "notPublished", StringComparison.Ordinal))
        {
            return new MobileAppContentState(publishingState, null);
        }

        // Intune's untyped singleton is the only route that exposes the derived
        // committedContentVersion property for macOSPkgApp, but the backend can intermittently return
        // a generic 400 for this read. Retry only this idempotent GET; malformed OData requests still
        // fail after the bounded attempts and mutation requests are never retried on 400.
        var requestUri = AppPath(appId);
        for (var attempt = 0; ; attempt++)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            try
            {
                var body = await GraphResponseReader
                    .ReadJsonAsync<MobileLobAppContentStateResponse>(response, requestUri, cancellationToken)
                    .ConfigureAwait(false);
                return new MobileAppContentState(body.PublishingState, body.CommittedContentVersion);
            }
            catch (GraphRequestException ex) when (ex.StatusCode == (int)HttpStatusCode.BadRequest && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string AppPath(string appId)
        => $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";

    private static string ContentRootPath(string appId, string oDataType)
        => $"{AppPath(appId)}/{ToGraphTypeSegment(oDataType)}";

    private static string ContentVersionPath(string appId, string contentVersionId, string oDataType)
        => $"{ContentRootPath(appId, oDataType)}/contentVersions/{Uri.EscapeDataString(contentVersionId)}";

    private static string FilePath(string appId, string contentVersionId, string fileId, string oDataType)
        => $"{ContentVersionPath(appId, contentVersionId, oDataType)}/files/{Uri.EscapeDataString(fileId)}";

    /// <summary>
    /// The OData type-cast path segments this client is known to build a URL for. This is a route
    /// element, not a data value, so it is validated against this fixed set rather than percent-encoded:
    /// <see cref="Uri.EscapeDataString(string)"/> would silently corrupt the route for any input outside
    /// this set instead of failing loudly, and every caller in this codebase only ever passes one of
    /// these three (<see cref="WindowsAppPublisher"/>, <see cref="MacOsAppPayloadMapper"/>).
    /// </summary>
    private static readonly HashSet<string> KnownGraphTypeSegments = new(StringComparer.Ordinal)
    {
        "microsoft.graph.win32LobApp",
        "microsoft.graph.macOSPkgApp",
        "microsoft.graph.macOSLobApp",
    };

    private static string ToGraphTypeSegment(string oDataType)
    {
        if (string.IsNullOrWhiteSpace(oDataType))
        {
            throw new GraphRequestException(
                "OData type must be provided for mobile app content operations.", null, null, null);
        }

        var segment = oDataType.StartsWith("#", StringComparison.Ordinal) ? oDataType[1..] : oDataType;
        if (!KnownGraphTypeSegments.Contains(segment))
        {
            throw new GraphRequestException(
                $"'{oDataType}' is not a recognized OData type for mobile app content operations.", null, null, null);
        }

        return segment;
    }

    private sealed class GraphListPage<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }
}
