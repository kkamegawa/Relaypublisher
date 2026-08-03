using System.Net.Http.Json;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls needed to upload mobile LOB app content: create a content version and file, poll the
/// file's upload state, commit it, patch the app's committed content version and notes, and poll the
/// app's publishing state. See doc/issues/issue-003-intune-graph-win32.md "Content upload flow".
/// <c>useBeta</c> routes the whole call through <c>/beta/</c> instead of <c>/v1.0/</c>: content
/// sub-resources (contentVersions/files) are inherited from <c>mobileLobApp</c> and exist in both API
/// versions, but a <c>macOSPkgApp</c> parent id is itself beta-only
/// (https://learn.microsoft.com/graph/api/resources/intune-apps-macospkgapp), so every call touching
/// that app - including its content sub-resources - stays on <c>/beta/</c> for consistency.
/// </summary>
public interface IMobileAppContentClient
{
    /// <summary>Creates a new content version and returns its id.</summary>
    Task<string> CreateContentVersionAsync(string appId, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Creates a content file record and returns its id.</summary>
    Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Reads the current state of a content file, for polling <c>uploadState</c>.</summary>
    Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Requests a fresh Azure Storage SAS URI before the current one expires.</summary>
    Task RenewUploadAsync(string appId, string contentVersionId, string fileId, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Commits an uploaded file with its encryption info.</summary>
    Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, bool useBeta, CancellationToken cancellationToken);

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

    public async Task<string> CreateContentVersionAsync(string appId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta) + "/contentVersions";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, new MobileAppContentCreateRequest(), cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppContentResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id;
    }

    public async Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = ContentVersionPath(appId, contentVersionId, useBeta) + "/files";
        var request = new MobileAppContentFileCreateRequest { Name = name, Size = size, SizeEncrypted = sizeEncrypted };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id;
    }

    public async Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, useBeta);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenewUploadAsync(string appId, string contentVersionId, string fileId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, useBeta) + "/renewUpload";
        using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId, useBeta) + "/commit";
        var request = new CommitFileRequest { FileEncryptionInfo = fileEncryptionInfo };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, CommittedContentVersion = contentVersionId };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task PatchNotesAsync(string appId, string notes, string oDataType, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta);
        var request = new MobileAppMetadataPatchPayload { ODataType = oDataType, Notes = notes };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task<string> GetPublishingStateAsync(string appId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = AppPath(appId, useBeta) + "?$select=publishingState";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync<Win32LobAppPublishingStateResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.PublishingState;
    }

    private static string AppPath(string appId, bool useBeta)
        => $"{VersionSegment(useBeta)}/deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";

    private static string ContentVersionPath(string appId, string contentVersionId, bool useBeta)
        => $"{AppPath(appId, useBeta)}/contentVersions/{Uri.EscapeDataString(contentVersionId)}";

    private static string FilePath(string appId, string contentVersionId, string fileId, bool useBeta)
        => $"{ContentVersionPath(appId, contentVersionId, useBeta)}/files/{Uri.EscapeDataString(fileId)}";

    private static string VersionSegment(bool useBeta) => useBeta ? "/beta" : "/v1.0";

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
    {
        EnsureSuccess(response, requestUri);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        T? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new GraphRequestException(
                $"Graph returned a malformed body for '{requestUri}'.",
                (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
        }

        return body ?? throw new GraphRequestException(
            $"Graph returned an empty body for '{requestUri}'.",
            (int)response.StatusCode, GetHeader(response, "client-request-id"), GetHeader(response, "request-id"));
    }

    private static void EnsureSuccess(HttpResponseMessage response, string requestUri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new GraphRequestException(
            $"Graph request to '{requestUri}' returned {(int)response.StatusCode}.",
            (int)response.StatusCode,
            GetHeader(response, "client-request-id"),
            GetHeader(response, "request-id"));
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
