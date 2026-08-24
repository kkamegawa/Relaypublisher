namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// The naming rule IntuneWinAppUtil.exe uses for the <c>.intunewin</c> file it produces: the setup
/// file's base name with the extension replaced by <c>.intunewin</c>. Shared by <see cref="IntuneWinPackager"/>
/// (which locates the tool's output) and <see cref="Publishing.Win32LobAppPayloadMapper"/> (which reports the
/// same name to Graph as <c>win32LobApp.fileName</c>), so the two never drift apart.
/// </summary>
public static class IntuneWinNaming
{
    /// <param name="setupFile">The `Package.IntuneWin.SetupFile` value (relative to the staging directory).</param>
    /// <returns>The `.intunewin` file name IntuneWinAppUtil.exe writes for this setup file.</returns>
    public static string PackageFileNameFor(string setupFile)
        => Path.GetFileNameWithoutExtension(setupFile) + ".intunewin";
}
