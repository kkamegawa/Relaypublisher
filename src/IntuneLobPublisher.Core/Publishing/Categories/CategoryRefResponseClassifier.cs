namespace IntuneLobPublisher.Core.Publishing.Categories;

/// <summary>
/// Decides whether a failed category <c>$ref</c> response actually means "the relationship is already
/// in the state we wanted", which makes the operation idempotent under
/// <see cref="GraphRetryHandler"/> replays (it buffers and resends request bodies, POST included) and
/// under a concurrent run that got there first.
/// <para>
/// This lives on its own because the exact error shape Intune returns for a duplicate category
/// <c>$ref</c> POST is not documented on Learn: only the OData relationship convention is. The
/// matching is therefore deliberately narrow - a specific "already exists / already linked" signal on
/// a 400/409 - so an unrelated 400 or 409 keeps failing instead of being silently treated as success.
/// When the observed service shape turns out to differ, this is the single place to correct.
/// </para>
/// </summary>
public static class CategoryRefResponseClassifier
{
    /// <summary>
    /// Signals seen for "this reference already exists" on OData <c>$ref</c> POSTs. Matched
    /// case-insensitively against Graph's <c>error.code</c> and <c>error.message</c>.
    /// </summary>
    private static readonly string[] AlreadyRelatedSignals =
    [
        "already exist",
        "already linked",
        "objectsalreadylinked",
        "duplicate reference",
        "reference already",
    ];

    /// <summary>
    /// True when a failed <c>POST .../categories/$ref</c> means the app is already related to that
    /// category. Only 400/409 responses carrying an explicit "already exists" signal qualify; every
    /// other 4xx stays a failure.
    /// </summary>
    public static bool IsAlreadyRelated(int statusCode, string? errorCode, string? errorMessage)
    {
        if (statusCode is not (400 or 409))
        {
            return false;
        }

        return Matches(errorCode) || Matches(errorMessage);
    }

    /// <summary>
    /// True when a failed <c>DELETE .../categories/{id}/$ref</c> means the relationship was already
    /// gone. Only 404 qualifies; an undecidable 400/409 stays a failure.
    /// </summary>
    public static bool IsAlreadyUnrelated(int statusCode) => statusCode == 404;

    private static bool Matches(string? value)
        => value is not null
           && Array.Exists(AlreadyRelatedSignals, signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
