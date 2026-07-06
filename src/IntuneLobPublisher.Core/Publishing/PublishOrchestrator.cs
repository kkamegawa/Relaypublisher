using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Runs the per-app publish flow in the doc/00-overview.md 6.10 order: resolve the app, evaluate
/// the version guard, create/update the win32LobApp, upload content, then plan and apply
/// assignments. Dry-run computes and reports the same information without any Graph write.
/// The non-windows platform skip is defense-in-depth: today <c>AppManifestValidator</c> rejects
/// non-windows entries before a request reaches here, so it is only reachable via callers that
/// bypass that validator or once a future platform (e.g. macOS) is added to the manifest schema.
/// </summary>
public interface IPublishOrchestrator
{
    /// <param name="reportAssignmentPlan">
    /// Invoked with the computed assignment plan before it is applied (issue-004: the full plan is
    /// shown before applying). Also invoked in dry-run.
    /// </param>
    Task<PublishResult> PublishAsync(
        PublishRequest request,
        Action<AssignmentPlan>? reportAssignmentPlan,
        CancellationToken cancellationToken);
}

public sealed class PublishOrchestrator : IPublishOrchestrator
{
    /// <summary>Synthetic app id used in dry-run plans for apps that do not exist yet.</summary>
    public const string NewAppPlaceholderId = "(new app)";

    private readonly IntuneAppResolver _resolver;
    private readonly IWin32LobAppClient _appClient;
    private readonly IWin32LobAppContentUploadOrchestrator _contentOrchestrator;
    private readonly IAssignmentService _assignmentService;
    private readonly ContentUploadOptions _contentUploadOptions;
    private readonly ILogger<PublishOrchestrator> _logger;

    public PublishOrchestrator(
        IntuneAppResolver resolver,
        IWin32LobAppClient appClient,
        IWin32LobAppContentUploadOrchestrator contentOrchestrator,
        IAssignmentService assignmentService,
        ILogger<PublishOrchestrator> logger,
        ContentUploadOptions? contentUploadOptions = null)
    {
        _resolver = resolver;
        _appClient = appClient;
        _contentOrchestrator = contentOrchestrator;
        _assignmentService = assignmentService;
        _contentUploadOptions = contentUploadOptions ?? new ContentUploadOptions();
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(
        PublishRequest request,
        Action<AssignmentPlan>? reportAssignmentPlan,
        CancellationToken cancellationToken)
    {
        var manifest = request.Manifest;
        var app = request.App;
        var packageIdentifier = Require(manifest.PackageIdentifier, nameof(manifest.PackageIdentifier));
        var packageVersion = Require(manifest.PackageVersion, nameof(manifest.PackageVersion));
        var platform = Require(app.Platform, nameof(app.Platform));
        var architecture = Require(app.Architecture, nameof(app.Architecture));
        var displayName = Require(app.DisplayName, nameof(app.DisplayName));

        // Defense-in-depth, not currently reachable via the CLI: ManifestValues.Platforms only
        // allows "windows" today, so AppManifestValidator rejects non-windows entries before this
        // code runs. This guard exists for callers that build a PublishRequest without going
        // through that validator, and to fail safely once macOS is added to the schema
        // (doc/00-overview.md section 16, roadmap item 12) before its publish flow is implemented.
        if (!string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase))
        {
            var reason = $"Platform '{platform}' has no publish flow yet; only windows is supported.";
            _logger.LogWarning(
                "Skipping {PackageIdentifier} {Platform}-{Architecture}: {Reason}",
                packageIdentifier, platform, architecture, reason);
            return new PublishResult(PublishOutcome.SkippedPlatformNotSupported, null, false, null, null, reason);
        }

        var identity = new AppIdentity(packageIdentifier, platform, architecture);
        var resolution = await _resolver.ResolveAsync(identity, displayName, cancellationToken).ConfigureAwait(false);

        if (PublishGuard.EvaluateVersion(resolution.Metadata?.PackageVersion, packageVersion, request.AllowDowngrade)
            == VersionGuardResult.SkipDowngrade)
        {
            // The whole manifest entry is stale, so assignments are not applied either.
            var reason =
                $"Manifest version '{packageVersion}' is lower than the published version " +
                $"'{resolution.Metadata!.PackageVersion}'. Use --allow-downgrade to publish anyway.";
            _logger.LogWarning(
                "Skipping {PackageIdentifier} {Platform}-{Architecture}: {Reason}",
                identity.PackageIdentifier, identity.Platform, identity.Architecture, reason);
            return new PublishResult(PublishOutcome.SkippedDowngrade, resolution.AppId, false, null, null, reason);
        }

        var artifacts = await PackageMetadataReader.ReadAsync(request.PackageDirectory, identity, cancellationToken)
            .ConfigureAwait(false);
        var package = ToPackageResult(identity, artifacts);
        var managementMetadata = new ManagementMetadata
        {
            PackageIdentifier = identity.PackageIdentifier,
            PackageVersion = packageVersion,
            Platform = identity.Platform,
            Architecture = identity.Architecture,
            ManifestPath = request.ManifestRepoRelativePath,
            ManifestHash = InputHashCalculator.ComputeManifestHash(manifest),
            InputHash = artifacts.Metadata.InputHash,
            SourceCommit = request.SourceCommit,
        };

        var detectionScript = await ReadDetectionScriptAsync(request, app, cancellationToken).ConfigureAwait(false);
        var iconBytes = await ReadIconAsync(request, manifest, cancellationToken).ConfigureAwait(false);
        // Mapped in dry-run too, so mapping errors (unknown Windows release, icon format) surface there.
        var payload = Win32LobAppPayloadMapper.Map(manifest, app, detectionScript, iconBytes);
        var syncMode = AssignmentSyncModes.Parse(manifest.AssignmentSync);

        if (request.DryRun)
        {
            return await DryRunAsync(request, resolution, artifacts, syncMode, reportAssignmentPlan, cancellationToken)
                .ConfigureAwait(false);
        }

        string appId;
        var appCreated = false;
        if (resolution.Outcome == AppResolutionOutcome.NotFound)
        {
            // Create carries the metadata in `notes` so the new app is never metadata-less.
            var createPayload = Win32LobAppPayloadMapper.Map(
                manifest, app, detectionScript, iconBytes, managementMetadata.Serialize());
            appId = await _appClient.CreateAppAsync(createPayload, cancellationToken).ConfigureAwait(false);
            appCreated = true;
            _logger.LogInformation(
                "Created app {AppId} for {PackageIdentifier} {Platform}-{Architecture}",
                appId, identity.PackageIdentifier, identity.Platform, identity.Architecture);
        }
        else
        {
            appId = resolution.AppId!;
            await _appClient.UpdateAppAsync(appId, payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Updated app {AppId} for {PackageIdentifier} {Platform}-{Architecture}",
                appId, identity.PackageIdentifier, identity.Platform, identity.Architecture);
        }

        // Adopted apps have null resolution.Metadata, so content is always uploaded and the
        // notes refresh inside the content flow performs the adopt write-back.
        var contentResult = await _contentOrchestrator.PublishContentAsync(
                appId, package, resolution.Metadata?.InputHash, managementMetadata, _contentUploadOptions, cancellationToken)
            .ConfigureAwait(false);

        var plan = await _assignmentService.CreatePlanAsync(appId, app, syncMode, cancellationToken).ConfigureAwait(false);
        reportAssignmentPlan?.Invoke(plan);
        await _assignmentService.ApplyAsync(plan, app, cancellationToken).ConfigureAwait(false);

        return new PublishResult(PublishOutcome.Published, appId, appCreated, contentResult.Outcome, plan, null);
    }

    private async Task<PublishResult> DryRunAsync(
        PublishRequest request,
        AppResolution resolution,
        PackageArtifacts artifacts,
        AssignmentSyncMode syncMode,
        Action<AssignmentPlan>? reportAssignmentPlan,
        CancellationToken cancellationToken)
    {
        var app = request.App;
        AssignmentPlan plan;
        if (resolution.Outcome == AppResolutionOutcome.NotFound)
        {
            // No app to read assignments from: everything the manifest wants shows as an add.
            plan = AssignmentPlanner.CreatePlan(NewAppPlaceholderId, app, syncMode, []);
            _logger.LogInformation(
                "Dry-run: would create app for {PackageIdentifier} and upload content",
                request.Manifest.PackageIdentifier);
        }
        else
        {
            plan = await _assignmentService.CreatePlanAsync(resolution.AppId!, app, syncMode, cancellationToken)
                .ConfigureAwait(false);
            var contentDecision = PublishGuard.EvaluateContentUpload(
                resolution.Metadata?.InputHash, artifacts.Metadata.InputHash);
            _logger.LogInformation(
                "Dry-run: would update app {AppId} and {ContentAction}",
                resolution.AppId,
                contentDecision == ContentUploadDecision.Skip ? "skip content upload (inputHash unchanged)" : "upload content");
        }

        reportAssignmentPlan?.Invoke(plan);
        return new PublishResult(PublishOutcome.DryRunCompleted, resolution.AppId, false, null, plan, null);
    }

    private static IntuneWinPackageResult ToPackageResult(AppIdentity identity, PackageArtifacts artifacts)
        => new(
            identity.PackageIdentifier,
            identity.Platform,
            identity.Architecture,
            artifacts.IntuneWinPath,
            artifacts.Metadata.IntuneWinSha256,
            artifacts.Metadata.InputHash,
            artifacts.Metadata.Tool.Version,
            artifacts.Metadata.Tool.Sha256,
            Path.Combine(Path.GetDirectoryName(artifacts.IntuneWinPath)!, PackageMetadataJson.FileName));

    private static string Require(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ManifestLoadException($"Manifest field '{fieldName}' is required for publish.")
            : value;

    private static async Task<string> ReadDetectionScriptAsync(
        PublishRequest request, Manifests.AppManifest app, CancellationToken cancellationToken)
    {
        var scriptPath = PathSafety.ResolveWithin(request.RepositoryRoot, app.Detection!.ScriptFile!, "Detection.ScriptFile");
        if (!File.Exists(scriptPath))
        {
            throw new ManifestLoadException($"Detection script '{app.Detection.ScriptFile}' does not exist under '{request.RepositoryRoot}'.");
        }

        return await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadIconAsync(
        PublishRequest request, Manifests.IntunePackageManifest manifest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(manifest.Icon))
        {
            return null;
        }

        var iconPath = PathSafety.ResolveWithin(request.RepositoryRoot, manifest.Icon, "Icon");
        if (!File.Exists(iconPath))
        {
            throw new ManifestLoadException($"Icon '{manifest.Icon}' does not exist under '{request.RepositoryRoot}'.");
        }

        return await File.ReadAllBytesAsync(iconPath, cancellationToken).ConfigureAwait(false);
    }
}
