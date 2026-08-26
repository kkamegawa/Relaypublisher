using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>One manifest app entry to preflight before any Graph write in the batch.</summary>
/// <param name="ManifestPath">The manifest file's own path (repo-root relative or absolute, caller's choice), carried through so a preflight failure can still be reported against the right manifest without re-deriving it from the original publish entry.</param>
public sealed record PreflightItem(
    IntunePackageManifest Manifest,
    AppManifest App,
    AppIdentity Identity,
    string EntryLabel,
    string ManifestPath);

/// <summary>
/// A preflighted entry, ready to publish. <see cref="Warnings"/> is empty for every non-macOS platform,
/// since only macOS PKG inspection produces semantic warnings today.
/// </summary>
public sealed record PreflightEntry(
    PreflightItem Item,
    PackageArtifacts Artifacts,
    IReadOnlyList<PkgInspectionWarning> Warnings,
    string? SelectedPrimaryBundleId);

/// <summary>One entry that failed local (non-Graph) preflight checks.</summary>
public sealed record PreflightFailure(PreflightItem Item, string Message);

/// <summary>
/// Aggregate preflight result for a publish batch. The batch may proceed to Graph only when
/// <see cref="Failures"/> is empty and every entry's <see cref="PreflightEntry.Warnings"/> has been
/// acknowledged (doc/00-overview.md 6.21, issue #116).
/// </summary>
public sealed record PublishPreflightResult(
    IReadOnlyList<PreflightEntry> Entries,
    IReadOnlyList<PreflightFailure> Failures);

/// <summary>
/// Runs the all-entry, zero-Graph-write preflight required before publish starts mutating: static
/// validation already ran by the time this is called (doc/00-overview.md 6.21 layer 3). This step
/// verifies packaged artifacts still match their manifest, without contacting Graph.
///
/// For macOS entries, this rehashes and re-inspects the staged <c>.pkg</c> so a stale, corrupt or
/// tampered artifact is caught before the first Graph call. For every other platform it verifies the
/// package artifact exists and matches identity, mirroring what <see cref="PublishOrchestrator"/> did
/// before this preflight existed. A single entry's failure never stops the rest: every entry is checked
/// so the whole batch's problems are reported at once, and the caller decides whether any failure blocks
/// the batch (it always should - see doc/00-overview.md 6.21 "all entries complete before the first write").
/// </summary>
public sealed class PublishPreflight(IPkgBundleInspector inspector, ILogger<PublishPreflight> logger)
{
    public async Task<PublishPreflightResult> RunAsync(
        IReadOnlyList<PreflightItem> items,
        string packageDirectory,
        string? expectedCliVersion,
        CancellationToken cancellationToken)
    {
        var entries = new List<PreflightEntry>();
        var failures = new List<PreflightFailure>();

        foreach (var item in items)
        {
            try
            {
                if (string.Equals(item.Identity.Platform, "macos", StringComparison.OrdinalIgnoreCase))
                {
                    var verification = await PackageMetadataReader.ReadAndVerifyAsync(
                        packageDirectory,
                        item.Identity,
                        item.Manifest,
                        item.App,
                        inspector,
                        expectedCliVersion,
                        cancellationToken).ConfigureAwait(false);
                    entries.Add(new PreflightEntry(
                        item,
                        verification.Artifacts,
                        verification.FreshReport.Warnings,
                        verification.FreshReport.SelectedPrimaryBundleId));
                }
                else
                {
                    // Windows' recorded content hash is not deterministic (a random per-run encryption
                    // key), so there is nothing to re-hash; existence/identity is the whole check.
                    var artifacts = await PackageMetadataReader
                        .ReadAsync(packageDirectory, item.Identity, cancellationToken)
                        .ConfigureAwait(false);
                    entries.Add(new PreflightEntry(item, artifacts, [], null));
                }
            }
            catch (PublisherException ex)
            {
                logger.LogWarning(
                    "Preflight failed for {EntryLabel}: {Message}", item.EntryLabel, ex.Message);
                failures.Add(new PreflightFailure(item, ex.Message));
            }
        }

        return new PublishPreflightResult(entries, failures);
    }
}
