using FluentValidation;
using FluentValidation.Results;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>FluentValidation based implementation of <see cref="IManifestValidator"/>.</summary>
public sealed class ManifestValidator : IManifestValidator
{
    static ManifestValidator()
    {
        // Keep validation messages in English regardless of the OS locale so CI logs stay deterministic.
        ValidatorOptions.Global.LanguageManager.Culture = System.Globalization.CultureInfo.InvariantCulture;
    }

    private readonly IntunePackageManifestValidator _validator = new();

    public ValidationResult Validate(IntunePackageManifest manifest)
        => _validator.Validate(manifest);
}

internal sealed class IntunePackageManifestValidator : AbstractValidator<IntunePackageManifest>
{
    public IntunePackageManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(m => m.SchemaVersion)
            .NotEmpty()
            .Must(v => ManifestValues.HasSupportedSchemaMajor(v!))
            .WithMessage(m => $"SchemaVersion '{m.SchemaVersion}' has an unsupported major version. Supported major version: {ManifestValues.SupportedSchemaMajor}.");

        RuleFor(m => m.PackageIdentifier).NotEmpty();
        RuleFor(m => m.PackageName).NotEmpty();
        RuleFor(m => m.Publisher).NotEmpty();
        RuleFor(m => m.Description).NotEmpty();
        RuleFor(m => m.PackageVersion).NotEmpty();

        RuleFor(m => m.AssignmentSync)
            .Must(v => v is null || ManifestValues.AssignmentSyncModes.Contains(v))
            .WithMessage(m => $"AssignmentSync '{m.AssignmentSync}' is not supported. Allowed values: {string.Join(", ", ManifestValues.AssignmentSyncModes)}.");

        RuleFor(m => m.Icon)
            .Must(v => v is null || PathSafety.IsSafeRelativePath(v))
            .WithMessage("Icon must be a repository-relative path without traversal segments.");

        // Format checked here (pure, no I/O); existence and size need the repository root and are
        // checked separately by ManifestAssetValidator (issue #63).
        RuleFor(m => m.Icon)
            .Must(v => v is null || ManifestValues.IconExtensions.Contains(Path.GetExtension(v).ToLowerInvariant()))
            .WithMessage(m => $"Icon '{m.Icon}' has an unsupported file extension. Supported: {string.Join(", ", ManifestValues.IconExtensions)}.");

        // macOS AppType: lob (macOSLobApp) requires a top-level Icon or the app never shows in the
        // admin console list (doc/01-manifest-schema.md §5.4).
        RuleFor(m => m.Icon)
            .NotEmpty()
            .When(m => m.Apps.Any(a => a.Platform == "macos" && (a.AppType ?? ManifestValues.DefaultMacOsAppType) == "lob"))
            .WithMessage("Icon is required when any app entry has Platform 'macos' and AppType 'lob'.");

        RuleFor(m => m.Apps)
            .NotEmpty()
            .WithMessage("Apps is required and must contain at least one app entry.");

        RuleForEach(m => m.Apps).SetValidator(new AppManifestValidator());
    }
}

/// <summary>
/// Validates a single app entry. Most rules are platform-conditional: Windows entries keep the
/// original unconditional shape (Package/Install/script Detection required, Source/AppType forbidden);
/// macOS entries use Source/IncludedApps detection and forbid the Windows-only fields
/// (doc/01-manifest-schema.md §5.3/§5.4).
/// </summary>
internal sealed class AppManifestValidator : AbstractValidator<AppManifest>
{
    public AppManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(a => a.Platform)
            .NotEmpty()
            .Must(v => ManifestValues.Platforms.Contains(v))
            .WithMessage(a => $"Platform '{a.Platform}' is not supported. Supported platforms: {string.Join(", ", ManifestValues.Platforms)}.");

        RuleFor(a => a.Architecture)
            .NotEmpty()
            .Must(v => ManifestValues.Architectures.Contains(v))
            .WithMessage(a => $"Architecture '{a.Architecture}' is not supported. Supported architectures: {string.Join(", ", ManifestValues.Architectures)}.");

        RuleFor(a => a.InstallerType)
            .NotEmpty()
            .Must(IsSupportedInstallerType)
            .WithMessage(a => a.Platform == "macos"
                ? $"InstallerType '{a.InstallerType}' is not supported for Platform 'macos'. Supported installer types: {string.Join(", ", ManifestValues.MacOsInstallerTypes)}."
                : $"InstallerType '{a.InstallerType}' is not supported for Platform '{a.Platform}'. Supported installer types: {string.Join(", ", ManifestValues.WindowsInstallerTypes)}.");

        RuleFor(a => a.AppType)
            .Must((app, appType) => app.Platform != "windows" || appType is null)
            .WithMessage("AppType must not be set for Platform 'windows'; it only applies to macOS.")
            .Must((app, appType) => app.Platform != "macos" || appType is null || ManifestValues.MacOsAppTypes.Contains(appType))
            .WithMessage(a => $"AppType '{a.AppType}' is not supported for Platform 'macos'. Allowed values: {string.Join(", ", ManifestValues.MacOsAppTypes)}.");

        RuleFor(a => a.DisplayName).NotEmpty();

        // Package (Windows) and Source (macOS) are mutually exclusive per platform. SetValidator is a
        // no-op when the property is null, so it naturally skips the platform where the field is unused.
        RuleFor(a => a.Package)
            .Must((app, package) => app.Platform != "windows" || package is not null)
            .WithMessage("Package is required for Platform 'windows'.")
            .Must((app, package) => app.Platform != "macos" || package is null)
            .WithMessage("Package must not be set for Platform 'macos'; use Source instead.")
            .SetValidator(new WindowsPackageManifestValidator()!);

        RuleFor(a => a.Source)
            .Must((app, source) => app.Platform != "macos" || source is not null)
            .WithMessage("Source is required for Platform 'macos'.")
            .Must((app, source) => app.Platform != "windows" || source is null)
            .WithMessage("Source must not be set for Platform 'windows'; use Package instead.")
            .SetValidator(new SourceManifestValidator()!);

        // A macOS PKG has no install command line: Intune drives the .pkg installer itself.
        RuleFor(a => a.Install)
            .Must((app, install) => app.Platform != "windows" || install is not null)
            .WithMessage("Install is required for Platform 'windows'.")
            .Must((app, install) => app.Platform != "macos" || install is null)
            .WithMessage("Install must not be set for Platform 'macos'; PKG apps have no install command line.")
            .SetValidator(new InstallManifestValidator()!);

        RuleFor(a => a.Detection)
            .NotNull()
            .SetValidator(a => new DetectionManifestValidator(a.Platform, a.AppType)!);

        RuleFor(a => a.Requirements)
            .NotNull()
            .SetValidator(a => new RequirementsManifestValidator(a.Platform)!);

        // Pre/post-install scripts only exist on the Graph macOSPkgApp resource
        // (doc/00-overview.md §6.13); AppType: lob and Platform: windows have no such property.
        RuleFor(a => a.Scripts)
            .Must((app, scripts) => app.Platform != "windows" || scripts is null)
            .WithMessage("Scripts must not be set for Platform 'windows'; pre/post-install scripts only apply to macOS AppType 'pkg'.")
            .Must((app, scripts) => !IsMacOsLob(app) || scripts is null)
            .WithMessage("Scripts must not be set for macOS AppType 'lob'; pre/post-install scripts are only supported for AppType 'pkg'.")
            .SetValidator(new MacOsScriptsManifestValidator()!);

        RuleFor(a => a.Requirements!.Architecture)
            .Must((app, requirementsArchitecture) => string.Equals(requirementsArchitecture, app.Architecture, StringComparison.Ordinal))
            .When(a => a.Requirements?.Architecture is not null && a.Architecture is not null)
            .WithMessage(a => $"Requirements.Architecture '{a.Requirements!.Architecture}' must match the app Architecture '{a.Architecture}'.")
            .OverridePropertyName("Requirements.Architecture");

        RuleForEach(a => a.Assignments).SetValidator(new AssignmentManifestValidator());

        RuleFor(a => a.Assignments)
            .Must(HaveUniqueTargets)
            .WithMessage("Assignments contains duplicate targets. Each group or built-in target may appear only once per app entry.");

        // macOSPkgApp cannot uninstall (doc/issues/issue-004-assignment-merge.md). AppType defaults to pkg on macOS.
        RuleFor(a => a.Assignments)
            .Must((app, assignments) => !IsMacOsPkg(app) || assignments.TrueForAll(x => x.Intent != "uninstall"))
            .WithMessage("Intent 'uninstall' is not supported for macOS AppType 'pkg' apps.");

        // Categories are resolved against the tenant catalog at publish/dry-run time; validate only
        // checks the shape locally and never contacts Graph, so a name that does not exist in the
        // tenant is reported by the publish preflight instead (doc/01-manifest-schema.md §5.8).
        // Names are matched verbatim (no trimming, no Unicode normalization) and no count or length
        // limit is imposed here.
        RuleFor(a => a.Categories)
            .Must(categories => categories is null || categories.TrueForAll(c => !string.IsNullOrWhiteSpace(c)))
            .WithMessage("Categories must not contain empty or whitespace-only entries.")
            .Must(categories => categories is null || categories.TrueForAll(HasNoOuterWhitespace))
            .WithMessage("Categories entries must not have leading or trailing whitespace.")
            .Must(HaveUniqueCategoryNames)
            .WithMessage("Categories contains duplicate names. Category names are compared case-insensitively.");
    }

    private static bool HasNoOuterWhitespace(string? value)
        => value is not null && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool HaveUniqueCategoryNames(List<string>? categories)
        => categories is null
            || categories.Distinct(StringComparer.OrdinalIgnoreCase).Count() == categories.Count;

    private static bool IsSupportedInstallerType(AppManifest app, string? installerType) => app.Platform switch
    {
        "windows" => ManifestValues.WindowsInstallerTypes.Contains(installerType),
        "macos" => ManifestValues.MacOsInstallerTypes.Contains(installerType),
        // Unknown/empty platform is already reported by the Platform rule above.
        _ => true,
    };

    private static bool IsMacOsPkg(AppManifest app)
        => app.Platform == "macos" && (app.AppType ?? ManifestValues.DefaultMacOsAppType) == ManifestValues.DefaultMacOsAppType;

    private static bool IsMacOsLob(AppManifest app)
        => app.Platform == "macos" && app.AppType == "lob";

    private static bool HaveUniqueTargets(List<AssignmentManifest> assignments)
    {
        // Include and exclude assignments for the same group are different Graph targets
        // (groupAssignmentTarget vs exclusionGroupAssignmentTarget), so Mode is part of the key.
        var keys = assignments.Select(a =>
            $"{a.Target ?? ManifestValues.DefaultAssignmentTarget}|{a.GroupId?.ToLowerInvariant()}|{a.Mode ?? ManifestValues.DefaultAssignmentMode}");
        return keys.Distinct(StringComparer.Ordinal).Count() == assignments.Count;
    }
}

internal sealed class WindowsPackageManifestValidator : AbstractValidator<WindowsPackageManifest>
{
    public WindowsPackageManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(p => p.IntuneWin).NotNull();

        RuleFor(p => p.IntuneWin!.SetupFile)
            .NotEmpty()
            .When(p => p.IntuneWin is not null)
            .OverridePropertyName("IntuneWin.SetupFile");

        RuleForEach(p => p.RepositoryFiles).ChildRules(file =>
        {
            file.RuleFor(f => f.Source).NotEmpty();
            file.RuleFor(f => f.Destination).NotEmpty();
        });

        RuleForEach(p => p.ExternalFiles).SetValidator(new SourceManifestValidator());
    }
}

internal sealed class SourceManifestValidator : AbstractValidator<SourceManifest>
{
    public SourceManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(s => s.Type)
            .NotEmpty()
            .Must(v => ManifestValues.SourceTypes.Contains(v))
            .WithMessage(s => $"Source Type '{s.Type}' is not supported. Supported types: {string.Join(", ", ManifestValues.SourceTypes)}.");

        RuleFor(s => s.Destination).NotEmpty();

        RuleFor(s => s.Sha256)
            .NotEmpty()
            .Must(v => ManifestValues.IsValidSha256(v!))
            .WithMessage("Sha256 must be a 64 character hexadecimal string.");

        RuleFor(s => s.Url)
            .NotEmpty()
            .When(s => s.Type == "publicHttp")
            .WithMessage("Url is required for source Type 'publicHttp'.");

        RuleFor(s => s.Owner).NotEmpty().When(s => s.Type == "githubRelease");
        RuleFor(s => s.Repository).NotEmpty().When(s => s.Type == "githubRelease");
        RuleFor(s => s.Tag).NotEmpty().When(s => s.Type == "githubRelease");
        RuleFor(s => s.AssetName).NotEmpty().When(s => s.Type == "githubRelease");

        RuleFor(s => s.AccountName).NotEmpty().When(s => s.Type == "azureBlob");
        RuleFor(s => s.Container).NotEmpty().When(s => s.Type == "azureBlob");
        RuleFor(s => s.BlobName).NotEmpty().When(s => s.Type == "azureBlob");

        RuleFor(s => s.Auth!.Type)
            .Must(v => v is null || ManifestValues.AuthTypes.Contains(v))
            .When(s => s.Auth is not null)
            .WithMessage(s => $"Auth.Type '{s.Auth!.Type}' is not supported. Allowed values: {string.Join(", ", ManifestValues.AuthTypes)}.")
            .OverridePropertyName("Auth.Type");

        RuleFor(s => s.Auth!.SecretName)
            .NotEmpty()
            .When(s => s.Auth?.Type == "token")
            .WithMessage("Auth.SecretName is required when Auth.Type is 'token'.")
            .OverridePropertyName("Auth.SecretName");

        RuleFor(s => s.Auth!.Type)
            .Must(v => v != "workloadIdentity")
            .When(s => s.Type == "githubRelease" && s.Auth is not null)
            .WithMessage("Auth.Type 'workloadIdentity' is not supported for source Type 'githubRelease'. Use 'token' or 'none'.")
            .OverridePropertyName("Auth.Type");

        RuleFor(s => s.Auth)
            .Must(a => a?.Type == "workloadIdentity")
            .When(s => s.Type == "azureBlob")
            .WithMessage("Auth.Type 'workloadIdentity' is required for source Type 'azureBlob'. Use publicHttp for anonymously readable URLs.")
            .OverridePropertyName("Auth.Type");
    }
}

/// <summary>
/// Validates the macOS <c>Scripts</c> block (doc/01-manifest-schema.md §5.4.2): only path shape
/// and extension are checked here (pure, no I/O). Existence, size, BOM and shebang need the
/// repository root, so they are checked by <see cref="ManifestAssetValidator"/> instead.
/// </summary>
internal sealed class MacOsScriptsManifestValidator : AbstractValidator<MacOsScriptsManifest>
{
    public MacOsScriptsManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(s => s)
            .Must(s => s.PreInstall is not null || s.PostInstall is not null)
            .WithMessage("Scripts must set at least one of PreInstall or PostInstall.")
            .OverridePropertyName("Scripts");

        RuleFor(s => s.PreInstall)
            .Must(v => v is null || PathSafety.IsSafeRelativePath(v))
            .WithMessage("Scripts.PreInstall must be a repository-relative path without traversal segments.")
            .Must(v => v is null || HasScriptExtension(v))
            .WithMessage(s => $"Scripts.PreInstall '{s.PreInstall}' must have the '{ManifestValues.MacOsScriptExtension}' extension.");

        RuleFor(s => s.PostInstall)
            .Must(v => v is null || PathSafety.IsSafeRelativePath(v))
            .WithMessage("Scripts.PostInstall must be a repository-relative path without traversal segments.")
            .Must(v => v is null || HasScriptExtension(v))
            .WithMessage(s => $"Scripts.PostInstall '{s.PostInstall}' must have the '{ManifestValues.MacOsScriptExtension}' extension.");
    }

    private static bool HasScriptExtension(string path)
        => string.Equals(Path.GetExtension(path), ManifestValues.MacOsScriptExtension, StringComparison.OrdinalIgnoreCase);
}

internal sealed class InstallManifestValidator : AbstractValidator<InstallManifest>
{
    public InstallManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(i => i.CommandLine).NotEmpty();
        RuleFor(i => i.UninstallCommandLine).NotEmpty();

        RuleFor(i => i.InstallExperience)
            .NotEmpty()
            .Must(v => ManifestValues.InstallExperiences.Contains(v))
            .WithMessage(i => $"InstallExperience '{i.InstallExperience}' is not supported. Allowed values: {string.Join(", ", ManifestValues.InstallExperiences)}.");

        RuleFor(i => i.RestartBehavior)
            .NotEmpty()
            .Must(v => ManifestValues.RestartBehaviors.Contains(v))
            .WithMessage(i => $"RestartBehavior '{i.RestartBehavior}' is not supported. Allowed values: {string.Join(", ", ManifestValues.RestartBehaviors)}.");

        RuleForEach(i => i.ReturnCodes).ChildRules(code =>
        {
            code.RuleFor(c => c.Type)
                .NotEmpty()
                .Must(v => ManifestValues.ReturnCodeTypes.Contains(v))
                .WithMessage(c => $"ReturnCodes Type '{c.Type}' is not supported. Allowed values: {string.Join(", ", ManifestValues.ReturnCodeTypes)}.");
        }).When(i => i.ReturnCodes is not null);
    }
}

/// <summary>
/// Windows uses script detection (<see cref="DetectionManifest.Type"/> / <see cref="DetectionManifest.ScriptFile"/>).
/// macOS has no script detection: it always requires <see cref="DetectionManifest.IncludedApps"/>
/// and forbids the Windows-only fields (doc/01-manifest-schema.md §5.3/§5.4).
/// </summary>
internal sealed class DetectionManifestValidator : AbstractValidator<DetectionManifest>
{
    public DetectionManifestValidator(string? platform, string? appType)
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        var isWindows = platform == "windows";
        var isMacOs = platform == "macos";
        var isMacOsLob = isMacOs && appType == "lob";

        // A single Must (rather than NotEmpty().Must().When(..., CurrentValidator)) so the empty-check
        // itself is also conditional: With ApplyConditionTo.CurrentValidator the When would only gate
        // the Must, leaving NotEmpty unconditional and wrongly rejecting macOS entries (Type is null there).
        RuleFor(d => d.Type)
            .Must(v => !isWindows || (!string.IsNullOrEmpty(v) && ManifestValues.DetectionTypes.Contains(v)))
            .WithMessage(d => string.IsNullOrEmpty(d.Type)
                ? "Detection.Type is required for Platform 'windows'."
                : $"Detection.Type '{d.Type}' is not supported. Supported types: {string.Join(", ", ManifestValues.DetectionTypes)}.");

        RuleFor(d => d.ScriptFile)
            .NotEmpty()
            .When(d => isWindows && d.Type == "script")
            .WithMessage("Detection.ScriptFile is required when Detection.Type is 'script'.");

        RuleFor(d => d.Type)
            .Null()
            .When(_ => isMacOs)
            .WithMessage("Detection.Type must not be set for Platform 'macos'; macOS apps are detected via IncludedApps.");

        RuleFor(d => d.ScriptFile)
            .Null()
            .When(_ => isMacOs)
            .WithMessage("Detection.ScriptFile must not be set for Platform 'macos'.");

        RuleFor(d => d.PrimaryBundleId)
            .Null()
            .When(_ => isWindows)
            .WithMessage("Detection.PrimaryBundleId must not be set for Platform 'windows'.");

        RuleFor(d => d.PrimaryBundleId)
            .Must(value => value is null || !string.IsNullOrWhiteSpace(value))
            .When(_ => isMacOs)
            .WithMessage("Detection.PrimaryBundleId must not be empty or whitespace for Platform 'macos'.");

        RuleFor(d => d.IncludedApps)
            .NotEmpty()
            .When(_ => isMacOs, ApplyConditionTo.CurrentValidator)
            .WithMessage("Detection.IncludedApps is required and must contain at least one entry for Platform 'macos'.")
            .Must(entries => entries is null || entries.Count <= 500)
            .When(_ => isMacOs, ApplyConditionTo.CurrentValidator)
            .WithMessage("Detection.IncludedApps must contain at most 500 entries for Platform 'macos'.")
            .Must(HaveUniqueBundleIds)
            .When(_ => isMacOs, ApplyConditionTo.CurrentValidator)
            .WithMessage("Detection.IncludedApps contains duplicate BundleId values.");

        RuleForEach(d => d.IncludedApps)
            .ChildRules(entry =>
            {
                entry.RuleFor(e => e.BundleId).NotEmpty();
                entry.RuleFor(e => e.BundleVersion).NotEmpty();
                entry.RuleFor(e => e.BundleBuildVersion)
                    .NotEmpty()
                    .When(_ => isMacOsLob)
                    .WithMessage("BundleBuildVersion is required for macOS AppType 'lob'.");
            })
            .When(_ => isMacOs);

        RuleFor(d => d.PrimaryBundleId)
            .Must((d, primaryBundleId) => primaryBundleId is null
                || string.IsNullOrWhiteSpace(primaryBundleId)
                || MacOsBundleSelector.FindMatchingIndexes(primaryBundleId, d.IncludedApps!).Count == 1)
            // Only meaningful once IncludedApps itself is present - otherwise this would add a
            // second, less relevant "must match" error on top of the real root cause reported above.
            .When(d => isMacOs && d.IncludedApps is not null)
            .WithMessage(d => BuildPrimaryBundleError(d));
    }

    private static bool HaveUniqueBundleIds(List<IncludedAppManifest>? entries)
        => entries is null
            || entries.Where(entry => entry.BundleId is not null)
                .Select(entry => entry.BundleId!)
                .Distinct(StringComparer.Ordinal)
                .Count() == entries.Count(entry => entry.BundleId is not null);

    private static string BuildPrimaryBundleError(DetectionManifest detection)
    {
        var primary = detection.PrimaryBundleId;
        var candidates = detection.IncludedApps is null
            ? string.Empty
            : string.Join(", ", detection.IncludedApps.Select(entry => entry.BundleId ?? "<null>"));
        if (primary is not null && detection.IncludedApps is not null)
        {
            var matchCount = MacOsBundleSelector.FindMatchingIndexes(primary, detection.IncludedApps).Count;
            return matchCount == 0
                ? $"Detection.PrimaryBundleId '{primary}' did not match any IncludedApps BundleId. Candidates: {candidates}."
                : $"Detection.PrimaryBundleId '{primary}' matched more than one IncludedApps BundleId. Candidates: {candidates}.";
        }

        return "Detection.PrimaryBundleId must match exactly one IncludedApps BundleId.";
    }
}

/// <summary>
/// <see cref="RequirementsManifest.Architecture"/> only applies to Windows (it must match the app-level
/// Architecture, checked separately by <see cref="AppManifestValidator"/>); the macOS sample manifest
/// omits it, since macOS has no separate "requirements architecture" concept (doc/01-manifest-schema.md §5.3).
/// </summary>
internal sealed class RequirementsManifestValidator : AbstractValidator<RequirementsManifest>
{
    public RequirementsManifestValidator(string? platform)
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.MinimumOSVersion).NotEmpty();

        RuleFor(r => r.Architecture)
            .NotEmpty()
            .When(_ => platform == "windows", ApplyConditionTo.CurrentValidator);
    }
}

internal sealed class AssignmentManifestValidator : AbstractValidator<AssignmentManifest>
{
    public AssignmentManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(a => a.Target)
            .Must(v => v is null || ManifestValues.AssignmentTargets.Contains(v))
            .WithMessage(a => $"Assignment Target '{a.Target}' is not supported. Allowed values: {string.Join(", ", ManifestValues.AssignmentTargets)}.");

        RuleFor(a => a.GroupId)
            .NotEmpty()
            .Must(v => Guid.TryParse(v, out _))
            .When(a => EffectiveTarget(a) == "group")
            .WithMessage(a => $"GroupId '{a.GroupId}' must be a valid GUID for Target 'group'.");

        RuleFor(a => a.GroupId)
            .Null()
            .When(a => EffectiveTarget(a) != "group")
            .WithMessage(a => $"GroupId must not be set when Target is '{a.Target}'.");

        RuleFor(a => a.Mode)
            .Must(v => v is null || ManifestValues.AssignmentModes.Contains(v))
            .WithMessage(a => $"Assignment Mode '{a.Mode}' is not supported. Allowed values: {string.Join(", ", ManifestValues.AssignmentModes)}.");

        RuleFor(a => a.Intent)
            .NotEmpty()
            .When(a => EffectiveMode(a) == "include")
            .WithMessage("Intent is required for include assignments.");

        RuleFor(a => a.Intent)
            .Must(v => v is null || ManifestValues.AssignmentIntents.Contains(v))
            .WithMessage(a => $"Assignment Intent '{a.Intent}' is not supported. Allowed values: {string.Join(", ", ManifestValues.AssignmentIntents)}.");

        RuleFor(a => a.FilterId)
            .Must(v => v is null || Guid.TryParse(v, out _))
            .WithMessage(a => $"FilterId '{a.FilterId}' must be a valid GUID.");

        RuleFor(a => a.FilterMode)
            .NotEmpty()
            .Must(v => ManifestValues.FilterModes.Contains(v))
            .When(a => a.FilterId is not null)
            .WithMessage("FilterMode ('include' or 'exclude') is required when FilterId is set.");

        RuleFor(a => a.Settings!.Notifications)
            .Must(v => v is null || ManifestValues.NotificationValues.Contains(v))
            .When(a => a.Settings is not null)
            .WithMessage(a => $"Settings.Notifications '{a.Settings!.Notifications}' is not supported. Allowed values: {string.Join(", ", ManifestValues.NotificationValues)}.")
            .OverridePropertyName("Settings.Notifications");
    }

    private static string EffectiveTarget(AssignmentManifest assignment)
        => assignment.Target ?? ManifestValues.DefaultAssignmentTarget;

    private static string EffectiveMode(AssignmentManifest assignment)
        => assignment.Mode ?? ManifestValues.DefaultAssignmentMode;
}
