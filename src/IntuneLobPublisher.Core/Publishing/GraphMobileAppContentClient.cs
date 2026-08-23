using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls needed to upload mobile LOB app content: create a content version and file, poll the
/// file's upload state, commit it, patch the app's committed content version and notes, and poll the
/// app's publishing state. See doc/issues/issue-003-intune-graph-win32.md "Content upload flow".
/// <c>useBeta</c> routes the whole call through <c>/beta/</c> instead of <c>/v1.0/</c>: content
/// sub-resources (contentVersions/files) are inherited from <c>mobileLobApp</c> and generally require an
/// OData type-cast segment after the app id. The file DELETE operation is the exception: Graph registers
/// only its uncast route. A <c>macOSPkgApp</c> parent id is itself beta-only
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-macospkgapp), so every call touching
/// that app - including its content sub-resources - stays on <c>/beta/</c> for consistency.
/// </summary>
public interface IMobileAppContentClient
{
    /// <summary>Lists all content versions, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<MobileAppContentResponse>> ListContentVersionsAsync(
        string appId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Creates a new content version and returns its id.</summary>
    Task<string> CreateContentVersionAsync(string appId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Lists all files in one content version, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<MobileAppContentFileResponse>> ListContentFilesAsync(
        string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Creates a content file record and returns its id.</summary>
    Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Reads the current state of a content file, for polling <c>uploadState</c>.</summary>
    Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an uncommitted file through the uncast mobileAppContentFile route.
    /// A 404 is accepted as an idempotent retry.
    /// </summary>
    Task DeleteContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Requests a fresh Azure Storage SAS URI before the current one expires.</summary>
    Task RenewUploadAsync(string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Commits an uploaded file with its encryption info.</summary>
    Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Activates a content version by patching the app's <c>committedContentVersion</c>. Point of no return.</summary>
    Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Writes management metadata JSON to the app's <c>notes</c> field.</summary>
    Task PatchNotesAsync(string appId, string notes, string oDataType, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Reads the app's current <c>publishingState</c> ("notPublished", "processing" or "published").</summary>
    Task<string> GetPublishingStateAsync(string appId, bool useBeta, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>, which is expected to be
/// one built by <see cref="GraphClientFactory"/> (authentication + retry already wired). Builds absolute
/// <c>/v1.0/</c> or <c>/beta/</c> paths per call rather than relying on the client's base address, the
/// same technique <see cref="Assignments.AssignmentGraphClient"/> already uses.
/// </summary>
public sealed class GraphMobileAppContentClient : IMobileAppContentClient
{
    private readonly HttpClient _httpClient;

    public GraphMobileAppContentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MobileAppContentResponse>> ListContentVersionsAsync(
        string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var results = new List<MobileAppContentResponse>();
        string? requestUri = ContentRootPath(appId, oDataType, useBeta) + "/contentVersions?$select=id";

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

    public async Task<string> CreateContentVersionAsync(string appId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = ContentRootPath(appId, oDataType, useBeta) + "/contentVersions";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, new MobileAppContentCreateRequest(), cancellationToken)
            .ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppContentResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a content version without an id for '{requestUri}'.");
    }

    public async Task<IReadOnlyList<MobileAppContentFileResponse>> ListContentFilesAsync(
        string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var results = new List<MobileAppContentFileResponse>();
        string? requestUri = ContentVersionPath(appId, contentVersionId, oDataType, useBeta)
            + "/files?$select=id,isCommitted,uploadState";

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
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = ContentVersionPath(appId, contentVersionId, oDataType, useBeta) + "/files";
        var request = new MobileAppContentFileCreateRequest { Name = name, Size = size, SizeEncrypted = sizeEncrypted };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id ?? throw GraphResponseReader.BodyFailure(
            response, $"Graph returned a content file without an id for '{requestUri}'.");
    }

    public async Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType, useBeta);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await GraphResponseReader.ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteContentFileAsync(
        string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        _ = ToGraphTypeSegment(oDataType);
        var requestUri = UntypedFilePath(appId, contentVersionId, fileId, useBeta);
        using var response = await _httpClient.DeleteAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenewUploadAsync(string appId, string contentVersionId, string fileId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType, useBeta) + "/renewUpload";
        using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, oDataType, useBeta) + "/commit";
        var request = new CommitFileRequest { FileEncryptionInfo = fileEncryptionInfo };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, CommittedContentVersion = contentVersionId };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchNotesAsync(string appId, string notes, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, Notes = notes };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        await GraphResponseReader.EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetPublishingStateAsync(string appId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta) + "?$select=publishingState";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var body = await GraphResponseReader.ReadJsonAsync<Win32LobAppPublishingStateResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.PublishingState;
    }

    private static string AppPath(string appId, bool useBeta)
        => $"{VersionSegment(useBeta)}/deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";

    private static string ContentRootPath(string appId, string oDataType, bool useBeta)
        => $"{AppPath(appId, useBeta)}/{ToGraphTypeSegment(oDataType)}";

    private static string ContentVersionPath(string appId, string contentVersionId, string oDataType, bool useBeta)
        => $"{ContentRootPath(appId, oDataType, useBeta)}/contentVersions/{Uri.EscapeDataString(contentVersionId)}";

    private static string FilePath(string appId, string contentVersionId, string fileId, string oDataType, bool useBeta)
        => $"{ContentVersionPath(appId, contentVersionId, oDataType, useBeta)}/files/{Uri.EscapeDataString(fileId)}";

    private static string UntypedFilePath(string appId, string contentVersionId, string fileId, bool useBeta)
        => $"{AppPath(appId, useBeta)}/contentVersions/{Uri.EscapeDataString(contentVersionId)}/files/{Uri.EscapeDataString(fileId)}";

    /// <summary>
    /// Validates the OData type-cast route segment instead of percent-encoding it. This client only
    /// supports the three concrete mobile LOB app types used by the publishers; rejecting anything
    /// else before the request prevents a malformed route and produces an actionable error.
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

    private static string VersionSegment(bool useBeta) => useBeta ? "/beta" : "/v1.0";

    private sealed class GraphListPage<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }
}
