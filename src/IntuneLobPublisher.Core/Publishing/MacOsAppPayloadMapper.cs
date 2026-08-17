using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Validation;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Which Graph resource type an app maps to, and which API version its calls must use.</summary>
/// <param name="UseBeta">True for <c>AppType: pkg</c> (macOSPkgApp is beta-only); false for <c>AppType: lob</c> (v1.0).</param>
/// <param name="ODataType">The `@odata.type` value used for create and for the notes/committedContentVersion PATCH calls.</param>
public sealed record MacOsAppTarget(bool UseBeta, string ODataType);

/// <summary>
/// Maps a validated macOS manifest entry to a Graph <see cref="MacOsPkgAppPayload"/> or
/// <see cref="MacOsLobAppPayload"/> (doc/00-overview.md §6.13). Pure mapping logic: no file I/O and no
/// Graph calls. Callers read icon bytes (subject to the usual manifest path-safety checks) and pass the
/// raw content in, same as <see cref="Win32LobAppPayloadMapper"/>.
/// </summary>
public static class MacOsAppPayloadMapper
{
    private const string PkgODataType = "#microsoft.graph.macOSPkgApp";
    private const string LobODataType = "#microsoft.graph.macOSLobApp";

    /// <summary>Resolves the target Graph resource type/API version from <see cref="AppManifest.AppType"/> without building a payload.</summary>
    public static MacOsAppTarget ResolveTarget(AppManifest app)
        => IsPkg(app)
            ? new MacOsAppTarget(UseBeta: true, PkgODataType)
            : new MacOsAppTarget(UseBeta: false, LobODataType);

    /// <param name="manifest">The root manifest, for top-level app info (description/publisher/owner/etc).</param>
    /// <param name="app">The platform/architecture-specific entry being published.</param>
    /// <param name="iconBytes">Raw icon bytes read from the repository, or null when the manifest has no `Icon` / it was not supplied.</param>
    /// <param name="scripts">
    /// Base64-encoded pre/post-install script content read from the repository, or null when the app
    /// entry has no `Scripts` block. Ignored for `AppType: lob` - Graph's `macOSLobApp` has no such
    /// property (doc/00-overview.md §6.13), and validation forbids `Scripts` there in the first place.
    /// </param>
    /// <param name="notes">Management metadata JSON for the `notes` field, set only on create requests.</param>
    /// <exception cref="Exceptions.UnsupportedMacOsVersionException">`Requirements.MinimumOSVersion` has no known mapping, or needs a beta-only flag unavailable to `AppType: lob`.</exception>
    /// <exception cref="Exceptions.UnsupportedIconFormatException">`iconBytes` was supplied but `Icon`'s extension is not recognized.</exception>
    public static MacOsAppPayloadBase Map(
        IntunePackageManifest manifest,
        AppManifest app,
        byte[]? iconBytes,
        MacOsAppScripts? scripts = null,
        string? notes = null)
    {
        var target = ResolveTarget(app);
        var includedApps = app.Detection!.IncludedApps!;
        var primary = includedApps[0];
        var minimumSupportedOperatingSystem = MacOsMinimumOperatingSystemTable.Map(app.Requirements!.MinimumOSVersion!, target.UseBeta);

        if (IsPkg(app))
        {
            return new MacOsPkgAppPayload
            {
                DisplayName = app.DisplayName!,
                Description = manifest.Description!,
                Publisher = manifest.Publisher!,
                Owner = manifest.Owner,
                Developer = manifest.Developer,
                InformationUrl = manifest.InformationUrl,
                LargeIcon = IconPayloadMapper.Map(manifest.Icon, iconBytes),
                RoleScopeTagIds = manifest.RoleScopeTagIds is { Count: > 0 } ? manifest.RoleScopeTagIds : null,
                FileName = app.Source!.Destination!,
                MinimumSupportedOperatingSystem = minimumSupportedOperatingSystem,
                IgnoreVersionDetection = app.Detection.IgnoreAppVersion ?? false,
                Notes = notes,
                PrimaryBundleId = primary.BundleId!,
                PrimaryBundleVersion = primary.BundleVersion!,
                IncludedApps = includedApps
                    .Select(a => new MacOsIncludedAppPayload { BundleId = a.BundleId!, BundleVersion = a.BundleVersion! })
                    .ToList(),
                PreInstallScript = ToScriptPayload(scripts?.PreInstall),
                PostInstallScript = ToScriptPayload(scripts?.PostInstall),
            };
        }

        return new MacOsLobAppPayload
        {
            DisplayName = app.DisplayName!,
            Description = manifest.Description!,
            Publisher = manifest.Publisher!,
            Owner = manifest.Owner,
            Developer = manifest.Developer,
            InformationUrl = manifest.InformationUrl,
            LargeIcon = IconPayloadMapper.Map(manifest.Icon, iconBytes),
            RoleScopeTagIds = manifest.RoleScopeTagIds is { Count: > 0 } ? manifest.RoleScopeTagIds : null,
            FileName = app.Source!.Destination!,
            MinimumSupportedOperatingSystem = minimumSupportedOperatingSystem,
            IgnoreVersionDetection = app.Detection.IgnoreAppVersion ?? false,
            Notes = notes,
            // macOSLobApp has no separate "primary bundle" concept; the first IncludedApps entry
            // stands in for both the top-level build/version numbers and the childApps list
            // (doc/01-manifest-schema.md §5.4: "the first entry is used for report display").
            BuildNumber = primary.BundleVersion!,
            VersionNumber = primary.BundleVersion!,
            ChildApps = includedApps
                .Select(a => new MacOsLobChildAppPayload { BundleId = a.BundleId!, BuildNumber = a.BundleVersion!, VersionNumber = a.BundleVersion! })
                .ToList(),
        };
    }

    private static bool IsPkg(AppManifest app) => (app.AppType ?? ManifestValues.DefaultMacOsAppType) == ManifestValues.DefaultMacOsAppType;

    private static MacOsAppScriptPayload? ToScriptPayload(string? scriptContent)
        => scriptContent is { } content ? new MacOsAppScriptPayload { ScriptContent = content } : null;
}
