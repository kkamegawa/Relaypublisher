namespace IntuneLobPublisher.Core.Manifests;

/// <summary>
/// Resolves the effective architecture of an app entry. Intune's macOS app resources
/// (macOSPkgApp/macOSLobApp) have no architecture property, so <c>Platform: macos</c> entries may omit
/// <see cref="AppManifest.Architecture"/>; the effective value is then "universal"
/// (doc/01-manifest-schema.md §5.3.1). <c>Platform: windows</c> entries have no default: a missing value
/// stays null and is rejected by validation, since it maps to the Graph <c>allowedArchitectures</c> value.
/// The resolved value must never be written back into <see cref="AppManifest.Architecture"/> — doing so
/// would make <c>InputHashCalculator</c> hash the resolved value instead of the declared one, breaking
/// hash compatibility for manifests that omit the field (doc/00-overview.md §6.7).
/// </summary>
public static class AppArchitecture
{
    public const string MacOsDefault = "universal";

    /// <summary>
    /// Returns the effective architecture for <paramref name="app"/>. Only a <c>null</c>
    /// <see cref="AppManifest.Architecture"/> on a macOS entry resolves to <see cref="MacOsDefault"/>; an
    /// empty or whitespace-only value is returned unchanged so validation still rejects it as invalid
    /// rather than silently treating it as omitted.
    /// </summary>
    public static string? Resolve(AppManifest app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app is { Platform: "macos", Architecture: null }
            ? MacOsDefault
            : app.Architecture;
    }
}
