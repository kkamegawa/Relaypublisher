using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Publishing.Assignments;
using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Everything needed to publish one manifest app entry.</summary>
/// <param name="Manifest">The root manifest, for app info, version and assignment sync mode.</param>
/// <param name="App">The platform/architecture entry being published.</param>
/// <param name="ManifestRepoRelativePath">Repository-relative manifest path stored in management metadata, using '/' separators.</param>
/// <param name="RepositoryRoot">Root the detection script and icon paths resolve against.</param>
/// <param name="PackageDirectory">Output directory produced by the package command.</param>
/// <param name="SourceCommit">Commit SHA recorded in management metadata.</param>
/// <param name="AllowDowngrade">Bypasses the version guard (doc/00-overview.md 6.8).</param>
/// <param name="DryRun">Report what would change without calling any Graph write.</param>
/// <param name="VerifiedArtifacts">
/// The package artifacts already re-verified by <see cref="PublishPreflight"/> before the batch's first
/// Graph write. When set, <see cref="PublishOrchestrator"/> uses this instead of re-reading metadata
/// from disk. Null callers (tests, library use outside the CLI's preflighted path) fall back to the
/// orchestrator's own <see cref="PackageMetadataReader.ReadAsync"/> call, unchanged from before issue #116.
/// </param>
public sealed record PublishRequest(
    IntunePackageManifest Manifest,
    AppManifest App,
    string ManifestRepoRelativePath,
    string RepositoryRoot,
    string PackageDirectory,
    string SourceCommit,
    bool AllowDowngrade,
    bool DryRun,
    PackageArtifacts? VerifiedArtifacts = null);

/// <summary>
/// The plan callbacks <see cref="IPublishOrchestrator"/> invokes before applying each kind of plan,
/// so the CLI can print the full diff first (issue-004). A dedicated object rather than one more
/// delegate parameter, so adding a plan kind does not keep changing the method signature; every
/// callback is optional and omitting the whole object reports nothing.
/// </summary>
public sealed class PublishReport
{
    /// <summary>Invoked with the computed category plan before it is applied, and in dry-run. Not invoked when the manifest omits <c>Categories</c>.</summary>
    public Action<CategoryPlan>? ReportCategoryPlan { get; init; }

    /// <summary>Invoked with the computed assignment plan before it is applied, and in dry-run.</summary>
    public Action<AssignmentPlan>? ReportAssignmentPlan { get; init; }
}

public enum PublishOutcome
{
    /// <summary>App metadata, content and assignments were applied.</summary>
    Published,

    /// <summary>Dry-run: the plan was computed and reported, nothing was written.</summary>
    DryRunCompleted,

    /// <summary>The manifest version is lower than the stored version; nothing was written (doc/00-overview.md 6.8).</summary>
    SkippedDowngrade,

    /// <summary>The entry's platform has no publish flow yet (macOS); nothing was written.</summary>
    SkippedPlatformNotSupported,
}

/// <summary>Result of publishing one manifest app entry.</summary>
/// <param name="CategoryPlan">
/// The computed category plan, when publishing got far enough to run the category preflight; null on
/// skips and on failures raised before it. A plan with <c>Requested == false</c> means the manifest
/// omitted <c>Categories</c>, which is distinct from "no plan was computed".
/// </param>
/// <param name="AssignmentPlan">The computed assignment plan, when publishing got that far; null on skips.</param>
/// <param name="SkipReason">Human-readable reason for skip outcomes, null otherwise.</param>
public sealed record PublishResult(
    PublishOutcome Outcome,
    string? AppId,
    bool AppCreated,
    ContentUploadOutcome? ContentOutcome,
    AssignmentPlan? AssignmentPlan,
    string? SkipReason,
    CategoryPlan? CategoryPlan = null);
