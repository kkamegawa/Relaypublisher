using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>How an existing app (if any) was resolved for a manifest entry.</summary>
public enum AppResolutionOutcome
{
    /// <summary>No existing app matched; a new app should be created.</summary>
    NotFound,

    /// <summary>Matched via management metadata in `notes`.</summary>
    ResolvedByMetadata,

    /// <summary>
    /// Matched via DisplayName fallback because no app carried matching metadata. The app should be
    /// "adopted": the caller must write management metadata back to `notes` before/with the next update.
    /// </summary>
    ResolvedByDisplayNameAdopted,
}

/// <summary>Result of resolving a manifest entry's <see cref="AppIdentity"/> against Intune apps.</summary>
public sealed record AppResolution(AppResolutionOutcome Outcome, string? AppId, ManagementMetadata? Metadata)
{
    public bool NeedsNotesWriteBack => Outcome == AppResolutionOutcome.ResolvedByDisplayNameAdopted;

    public static readonly AppResolution NotFound = new(AppResolutionOutcome.NotFound, null, null);
}

/// <summary>
/// Resolves the Intune app that owns a manifest entry (doc/00-overview.md 6.1): first by
/// management metadata in `notes`, falling back to an exact DisplayName match. Either lookup
/// matching more than one app fails without writing anything.
/// </summary>
public sealed class IntuneAppResolver
{
    private readonly IIntuneAppDirectory _directory;

    public IntuneAppResolver(IIntuneAppDirectory directory)
    {
        _directory = directory;
    }

    public async Task<AppResolution> ResolveAsync(AppIdentity identity, string displayName, CancellationToken cancellationToken)
    {
        var apps = await _directory.ListAppsAsync(cancellationToken).ConfigureAwait(false);

        var metadataMatches = new List<(string Id, ManagementMetadata Metadata)>();
        foreach (var app in apps)
        {
            if (ManagementMetadata.TryParse(app.Notes, out var metadata) && identity.Matches(metadata!))
            {
                metadataMatches.Add((app.Id, metadata!));
            }
        }

        if (metadataMatches.Count > 1)
        {
            throw new AmbiguousAppMatchException(
                $"Management metadata for '{identity.PackageIdentifier}' ({identity.Platform}/{identity.Architecture}) " +
                $"matched {metadataMatches.Count} Intune apps; refusing to write.",
                metadataMatches.Select(m => m.Id).ToList());
        }

        if (metadataMatches.Count == 1)
        {
            var (id, metadata) = metadataMatches[0];
            return new AppResolution(AppResolutionOutcome.ResolvedByMetadata, id, metadata);
        }

        var displayNameMatches = apps
            .Where(app => string.Equals(app.DisplayName, displayName, StringComparison.Ordinal))
            .ToList();

        if (displayNameMatches.Count > 1)
        {
            throw new AmbiguousAppMatchException(
                $"DisplayName '{displayName}' matched {displayNameMatches.Count} Intune apps; refusing to write.",
                displayNameMatches.Select(a => a.Id).ToList());
        }

        if (displayNameMatches.Count == 1)
        {
            return new AppResolution(AppResolutionOutcome.ResolvedByDisplayNameAdopted, displayNameMatches[0].Id, null);
        }

        return AppResolution.NotFound;
    }
}
