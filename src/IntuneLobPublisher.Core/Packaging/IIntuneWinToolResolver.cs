namespace IntuneLobPublisher.Core.Packaging;

/// <summary>How to locate (or acquire) IntuneWinAppUtil.exe.</summary>
/// <param name="ExplicitToolPath">Tool path given on the command line; highest priority.</param>
/// <param name="PinnedVersion">Release tag to use. When null the latest release is resolved.</param>
/// <param name="KnownSha256">Known-good SHA256 of the tool. When set, any resolved binary must match or resolution fails.</param>
/// <param name="ToolsDirectory">Directory downloaded tools are cached under, one subdirectory per version.</param>
public sealed record IntuneWinToolOptions(
    string? ExplicitToolPath,
    string? PinnedVersion,
    string? KnownSha256,
    string ToolsDirectory);

/// <summary>A usable IntuneWinAppUtil.exe. Version is null when a local path was supplied without a pin.</summary>
public sealed record ResolvedIntuneWinTool(string Path, string? Version, string Sha256);

/// <summary>
/// Locates IntuneWinAppUtil.exe: command-line option, then environment variable, then the
/// tools directory cache, downloading from the official repository when not cached.
/// </summary>
public interface IIntuneWinToolResolver
{
    Task<ResolvedIntuneWinTool> ResolveAsync(IntuneWinToolOptions options, CancellationToken cancellationToken);
}
