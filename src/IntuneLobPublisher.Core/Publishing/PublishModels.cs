using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing.Assignments;

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
public sealed record PublishRequest(
    IntunePackageManifest Manifest,
    AppManifest App,
    string ManifestRepoRelativePath,
    string RepositoryRoot,
    string PackageDirectory,
    string SourceCommit,
    bool AllowDowngrade,
    bool DryRun);

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
/// <param name="AssignmentPlan">The computed assignment plan, when publishing got that far; null on skips.</param>
/// <param name="SkipReason">Human-readable reason for skip outcomes, null otherwise.</param>
public sealed record PublishResult(
    PublishOutcome Outcome,
    string? AppId,
    bool AppCreated,
    ContentUploadOutcome? ContentOutcome,
    AssignmentPlan? AssignmentPlan,
    string? SkipReason);
