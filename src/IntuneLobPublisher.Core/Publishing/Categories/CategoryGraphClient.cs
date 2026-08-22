using System.Net.Http.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing.Categories;

public interface ICategoryGraphClient
{
    /// <summary>Lists the tenant's <c>mobileAppCategory</c> catalog, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<IntuneAppCategory>> ListTenantCategoriesAsync(bool useBeta, CancellationToken cancellationToken);

    /// <summary>Lists the categories currently related to one app, following <c>@odata.nextLink</c>.</summary>
    Task<IReadOnlyList<IntuneAppCategory>> ListAppCategoriesAsync(string appId, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Relates an existing tenant category to the app. Returns false when the relationship already existed.</summary>
    Task<bool> AddCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken);

    /// <summary>Unrelates a category from the app. Returns false when the relationship was already gone.</summary>
    Task<bool> RemoveCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken);
}

/// <summary>
/// Reads and writes Intune app category relationships through Microsoft Graph. Categories are a
/// <c>mobileApp</c> navigation relationship, not a scalar property, so the writes are OData
/// <c>$ref</c> operations and never touch the tenant-wide category resource itself:
/// <list type="bullet">
/// <item><c>GET  /{version}/deviceAppManagement/mobileAppCategories</c></item>
/// <item><c>GET  /{version}/deviceAppManagement/mobileApps/{appId}/categories</c></item>
/// <item><c>POST /{version}/deviceAppManagement/mobileApps/{appId}/categories/$ref</c></item>
/// <item><c>DELETE /{version}/deviceAppManagement/mobileApps/{appId}/categories/{categoryId}/$ref</c></item>
/// </list>
/// Each call builds an absolute <c>/v1.0/</c> or <c>/beta/</c> path, the same technique as
/// <see cref="Assignments.AssignmentGraphClient"/>, because the shared <see cref="HttpClient"/>'s base
/// address is <c>/v1.0/</c> while <c>macOSPkgApp</c> entries have to stay on beta.
/// </summary>
public sealed class CategoryGraphClient : ICategoryGraphClient
{
    private readonly HttpClient _httpClient;

    public CategoryGraphClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<IntuneAppCategory>> ListTenantCategoriesAsync(bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{VersionSegment(useBeta)}/deviceAppManagement/mobileAppCategories?$select=id,displayName";
        return await ListAsync(
            requestUri,
            "Failed to list Intune app categories.",
            // Every entry that declares Categories goes through this listing, so 401/403 here means the
            // identity cannot synchronize categories for any entry: report it identity-wide so the CLI
            // stops the batch instead of repeating the same permission error once per entry (#94).
            identityWideOnAccessDenied: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntuneAppCategory>> ListAppCategoriesAsync(string appId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{AppCategoriesPath(appId, useBeta)}?$select=id,displayName";
        return await ListAsync(
            requestUri,
            $"Failed to list categories for Intune app '{appId}'.",
            identityWideOnAccessDenied: false,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> AddCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{AppCategoriesPath(appId, useBeta)}/$ref";
        var payload = new CategoryReferencePayload { ODataId = BuildCategoryODataId(categoryId, useBeta) };
        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var failure = await GraphErrorReader.ReadFailureAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        // GraphRetryHandler replays buffered request bodies on 429/503, so a POST that already
        // succeeded can be sent twice; the same is true when a concurrent run added the relationship.
        // Only an explicit "already exists" answer is accepted as success here.
        if (CategoryRefResponseClassifier.IsAlreadyRelated(failure.StatusCode, failure.ErrorCode, failure.Summary))
        {
            return false;
        }

        throw failure.ToRequestException($"Failed to add category '{categoryId}' to Intune app '{appId}'.");
    }

    public async Task<bool> RemoveCategoryAsync(string appId, string categoryId, bool useBeta, CancellationToken cancellationToken)
    {
        var requestUri = $"{AppCategoriesPath(appId, useBeta)}/{Uri.EscapeDataString(categoryId)}/$ref";
        using var response = await _httpClient.DeleteAsync(requestUri, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var failure = await GraphErrorReader.ReadFailureAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        // A replayed DELETE (or a concurrent run) finds the relationship already gone: that is the
        // state the plan wanted, so it is a success.
        if (CategoryRefResponseClassifier.IsAlreadyUnrelated(failure.StatusCode))
        {
            return false;
        }

        throw failure.ToRequestException($"Failed to remove category '{categoryId}' from Intune app '{appId}'.");
    }

    /// <summary>
    /// The <c>@odata.id</c> of a tenant category. Built from the scheme and authority of the client's
    /// base address plus the API version of the request that carries it: the base address already ends
    /// in <c>/v1.0/</c>, so appending to it would produce a v1.0 reference inside a beta request and
    /// would hardcode the host for stub-server tests.
    /// </summary>
    private string BuildCategoryODataId(string categoryId, bool useBeta)
    {
        var baseAddress = _httpClient.BaseAddress
            ?? throw new CategorySyncException("The Graph HttpClient has no base address, so a category '@odata.id' cannot be built.");
        var authority = baseAddress.GetLeftPart(UriPartial.Authority);
        return $"{authority}{VersionSegment(useBeta)}/deviceAppManagement/mobileAppCategories/{Uri.EscapeDataString(categoryId)}";
    }

    private async Task<IReadOnlyList<IntuneAppCategory>> ListAsync(
        string initialRequestUri,
        string failurePrefix,
        bool identityWideOnAccessDenied,
        CancellationToken cancellationToken)
    {
        var results = new List<IntuneAppCategory>();
        string? requestUri = initialRequestUri;

        while (requestUri is not null)
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await GraphErrorReader
                    .ReadFailureAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
                throw identityWideOnAccessDenied && failure.StatusCode is 401 or 403
                    ? failure.ToAccessDeniedException(failurePrefix)
                    : failure.ToRequestException(failurePrefix);
            }

            var page = await GraphResponseReader
                .ReadJsonAsync<MobileAppCategoryListPage>(response, requestUri, cancellationToken).ConfigureAwait(false);

            foreach (var category in page.Value)
            {
                if (category.Id is null || category.DisplayName is null)
                {
                    throw GraphResponseReader.BodyFailure(
                        response, $"Graph returned an app category without an id or displayName for '{requestUri}'.");
                }

                results.Add(new IntuneAppCategory(category.Id, category.DisplayName));
            }

            requestUri = page.NextLink;
        }

        return results;
    }

    private static string AppCategoriesPath(string appId, bool useBeta)
        => $"{VersionSegment(useBeta)}/deviceAppManagement/mobileApps/{Uri.EscapeDataString(appId)}/categories";

    private static string VersionSegment(bool useBeta) => useBeta ? "/beta" : "/v1.0";

    private sealed class MobileAppCategoryListPage
    {
        [JsonPropertyName("value")]
        public List<MobileAppCategoryResponse> Value { get; init; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed class MobileAppCategoryResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }
    }
}

/// <summary>Body of a category <c>$ref</c> POST: an OData reference to an existing tenant category.</summary>
public sealed class CategoryReferencePayload
{
    [JsonPropertyName("@odata.id")]
    public required string ODataId { get; init; }
}
