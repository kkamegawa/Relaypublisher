namespace IntuneLobPublisher.Core.Publishing.Assignments;

/// <summary>Derives Graph endpoint variants from the configured base address.</summary>
public static class GraphEndpoints
{
    /// <summary>
    /// Returns the beta variant of <paramref name="baseAddress"/> by swapping a trailing
    /// <c>v1.0</c> path segment for <c>beta</c>. Assignment targets carry their filter
    /// properties (`deviceAndAppManagementAssignmentFilterId`/`Type`) only on beta, so all
    /// assignment operations use this base. Non-v1.0 addresses (test stub servers) pass
    /// through unchanged.
    /// </summary>
    public static Uri ToBeta(Uri baseAddress)
    {
        var text = baseAddress.AbsoluteUri;
        if (text.EndsWith("/v1.0/", StringComparison.Ordinal))
        {
            return new Uri(text[..^6] + "/beta/");
        }

        if (text.EndsWith("/v1.0", StringComparison.Ordinal))
        {
            return new Uri(text[..^5] + "/beta/");
        }

        return baseAddress;
    }
}
