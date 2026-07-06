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

        RuleFor(m => m.Apps)
            .NotEmpty()
            .WithMessage("Apps is required and must contain at least one app entry.");

        RuleForEach(m => m.Apps).SetValidator(new AppManifestValidator());
    }
}

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
            .Must(v => ManifestValues.WindowsInstallerTypes.Contains(v))
            .When(a => a.Platform == "windows", ApplyConditionTo.CurrentValidator)
            .WithMessage(a => $"InstallerType '{a.InstallerType}' is not supported for Platform 'windows'. Supported installer types: {string.Join(", ", ManifestValues.WindowsInstallerTypes)}.");

        RuleFor(a => a.DisplayName).NotEmpty();

        RuleFor(a => a.Package)
            .NotNull()
            .SetValidator(new WindowsPackageManifestValidator()!);

        RuleFor(a => a.Install)
            .NotNull()
            .SetValidator(new InstallManifestValidator()!);

        RuleFor(a => a.Detection)
            .NotNull()
            .SetValidator(new DetectionManifestValidator()!);

        RuleFor(a => a.Requirements)
            .NotNull()
            .SetValidator(new RequirementsManifestValidator()!);

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
    }

    private static bool IsMacOsPkg(AppManifest app)
        => app.Platform == "macos" && (app.AppType ?? "pkg") == "pkg";

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

internal sealed class DetectionManifestValidator : AbstractValidator<DetectionManifest>
{
    public DetectionManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(d => d.Type)
            .NotEmpty()
            .Must(v => ManifestValues.DetectionTypes.Contains(v))
            .WithMessage(d => $"Detection.Type '{d.Type}' is not supported. Supported types: {string.Join(", ", ManifestValues.DetectionTypes)}.");

        RuleFor(d => d.ScriptFile)
            .NotEmpty()
            .When(d => d.Type == "script")
            .WithMessage("Detection.ScriptFile is required when Detection.Type is 'script'.");
    }
}

internal sealed class RequirementsManifestValidator : AbstractValidator<RequirementsManifest>
{
    public RequirementsManifestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(r => r.MinimumOSVersion).NotEmpty();
        RuleFor(r => r.Architecture).NotEmpty();
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
