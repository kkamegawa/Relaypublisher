namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Minimal projection of an Intune mobile app needed for resolution matching.</summary>
public sealed record IntuneAppSummary(string Id, string? DisplayName, string? Notes);

/// <summary>
/// Read-only listing of Intune mobile apps, abstracted so <see cref="IntuneAppResolver"/> can be
/// unit tested without a real Graph endpoint. Resolution only ever reads through this interface;
/// it never writes, matching the "ambiguous match fails without writing" requirement.
/// </summary>
public interface IIntuneAppDirectory
{
    Task<IReadOnlyList<IntuneAppSummary>> ListAppsAsync(CancellationToken cancellationToken);
}
