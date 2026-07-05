using System.Net.Http.Json;
using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The Graph calls needed to upload Win32 app content: create a content version and file, poll the
/// file's upload state, commit it, patch the app's committed content version and notes, and poll the
/// app's publishing state. See doc/issues/issue-003-intune-graph-win32.md "Content upload flow".
/// </summary>
public interface IMobileAppContentClient
{
    /// <summary>Creates a new content version and returns its id.</summary>
    Task<string> CreateContentVersionAsync(string appId, CancellationToken cancellationToken);

    /// <summary>Creates a content file record and returns its id.</summary>
    Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, CancellationToken cancellationToken);

    /// <summary>Reads the current state of a content file, for polling <c>uploadState</c>.</summary>
    Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, CancellationToken cancellationToken);

    /// <summary>Requests a fresh Azure Storage SAS URI before the current one expires.</summary>
    Task RenewUploadAsync(string appId, string contentVersionId, string fileId, CancellationToken cancellationToken);

    /// <summary>Commits an uploaded file with its encryption info.</summary>
    Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, CancellationToken cancellationToken);

    /// <summary>Activates a content version by patching the app's <c>committedContentVersion</c>. Point of no return.</summary>
    Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, CancellationToken cancellationToken);

    /// <summary>Writes management metadata JSON to the app's <c>notes</c> field.</summary>
    Task PatchNotesAsync(string appId, string notes, CancellationToken cancellationToken);

    /// <summary>Reads the app's current <c>publishingState</c> ("notPublished", "processing" or "published").</summary>
    Task<string> GetPublishingStateAsync(string appId, CancellationToken cancellationToken);
}

/// <summary>
/// Calls Microsoft Graph using the caller-supplied <see cref="HttpClient"/>, which is expected to be
/// one built by <see cref="GraphClientFactory"/> (authentication + retry already wired). Follows the
/// same structure as <see cref="GraphIntuneAppDirectory"/>.
/// </summary>
public sealed class GraphMobileAppContentClient : IMobileAppContentClient
{
    private readonly HttpClient _httpClient;

    public GraphMobileAppContentClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> CreateContentVersionAsync(string appId, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/contentVersions";
        using var response = await _httpClient.PostAsJsonAsync(requestUri, new MobileAppContentCreateRequest(), cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppContentResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id;
    }

    public async Task<string> CreateContentFileAsync(
        string appId, string contentVersionId, string name, long size, long sizeEncrypted, CancellationToken cancellationToken)
    {
        var requestUri = ContentVersionPath(appId, contentVersionId) + "/files";
        var request = new MobileAppContentFileCreateRequest { Name = name, Size = size, SizeEncrypted = sizeEncrypted };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.Id;
    }

    public async Task<MobileAppContentFileResponse> GetContentFileAsync(
        string appId, string contentVersionId, string fileId, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<MobileAppContentFileResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenewUploadAsync(string appId, string contentVersionId, string fileId, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId) + "/renewUpload";
        using var response = await _httpClient.PostAsync(requestUri, content: null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task CommitFileAsync(
        string appId, string contentVersionId, string fileId, FileEncryptionInfoPayload fileEncryptionInfo, CancellationToken cancellationToken)
    {
        var requestUri = FilePath(appId, contentVersionId, fileId) + "/commit";
        var request = new CommitFileRequest { FileEncryptionInfo = fileEncryptionInfo };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task PatchCommittedContentVersionAsync(string appId, string contentVersionId, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        var request = new Win32LobAppContentPatchPayload { CommittedContentVersion = contentVersionId };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task PatchNotesAsync(string appId, string notes, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}";
        var request = new Win32LobAppContentPatchPayload { Notes = notes };
        using var response = await _httpClient.PatchAsync(requestUri, JsonContent.Create(request), cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, requestUri);
    }

    public async Task<string> GetPublishingStateAsync(string appId, CancellationToken cancellationToken)
    {
        var requestUri = $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}?$select=publishingState";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync<Win32LobAppPublishingStateResponse>(response, requestUri, cancellationToken).ConfigureAwait(false);
        return body.PublishingState;
    }

    private static string ContentVersionPath(string appId, string contentVersionId)
        => $"deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/contentVersions/{Uri.EscapeDataString(contentVersionId)}";

    private static string FilePath(string appId, string contentVersionId, string fileId)
        => $"{ContentVersionPath(appId, contentVersionId)}/files/{Uri.EscapeDataString(fileId)}";

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
