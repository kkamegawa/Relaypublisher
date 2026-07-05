namespace IntuneLobPublisher.Core.Packaging;

/// <summary>Fetches IntuneWinAppUtil.exe releases from the official distribution channel.</summary>
public interface IIntuneWinToolDownloader
{
    /// <summary>Returns the tag name of the latest release.</summary>
    Task<string> GetLatestVersionAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the tool binary for <paramref name="version"/> (a release tag) to <paramref name="destinationPath"/>.</summary>
    Task DownloadAsync(string version, string destinationPath, CancellationToken cancellationToken);
}
