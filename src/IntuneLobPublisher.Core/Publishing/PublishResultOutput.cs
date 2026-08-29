using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Machine-readable publish result for one manifest app entry.</summary>
/// <param name="CategoryOutcome">
/// Additive optional field (issue #99). <c>applied</c> when the category plan had at least one
/// add/remove to perform, <c>unchanged</c> when the manifest declared <c>Categories</c> and the diff
/// was empty, <c>not-requested</c> when a completed publish had no <c>Categories</c> in the manifest,
/// and null when publishing never reached a category write (skip, dry-run, or a failure before the
/// preflight). Per-category detail stays in console output and logs.
/// </param>
/// <param name="WarningCodes">
/// Additive optional field (issue #116). The macOS PKG semantic inspection warning codes raised for
/// this entry's preflight, or null when the entry is not macOS, had none, or publishing never reached
/// the preflight step. Present even when the batch failed because the warnings were not acknowledged.
/// </param>
/// <param name="ForceAcknowledged">
/// Additive optional field (issue #116). True when <c>--force</c> acknowledged this entry's semantic
/// warnings, false when an interactive confirmation acknowledged them, and null when there was nothing
/// to acknowledge or the entry never reached the preflight step.
/// </param>
public sealed record PublishResultEntry(
    [property: JsonPropertyName("packageIdentifier")] string PackageIdentifier,
    [property: JsonPropertyName("packageVersion")] string PackageVersion,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("appId")] string? AppId,
    [property: JsonPropertyName("contentOutcome")] string? ContentOutcome,
    [property: JsonPropertyName("skipReason")] string? SkipReason,
    [property: JsonPropertyName("categoryOutcome")] string? CategoryOutcome = null,
    [property: JsonPropertyName("warningCodes")] string[]? WarningCodes = null,
    [property: JsonPropertyName("forceAcknowledged")] bool? ForceAcknowledged = null);

/// <summary>Creates and writes stable JSON output for CI integrations.</summary>
public static class PublishResultOutput
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static PublishResultEntry FromResult(
        PublishRequest request,
        PublishResult result,
        string[]? warningCodes = null,
        bool? forceAcknowledged = null)
        => new(
            Require(request.Manifest.PackageIdentifier, nameof(request.Manifest.PackageIdentifier)),
            Require(request.Manifest.PackageVersion, nameof(request.Manifest.PackageVersion)),
            Require(request.App.Platform, nameof(request.App.Platform)),
            Require(AppArchitecture.Resolve(request.App), nameof(request.App.Architecture)),
            request.ManifestRepoRelativePath,
            ToWireValue(result.Outcome),
            result.AppId,
            result.ContentOutcome is null ? null : ToWireValue(result.ContentOutcome.Value),
            result.SkipReason,
            ToCategoryWireValue(result),
            warningCodes,
            forceAcknowledged);

    public static PublishResultEntry FromFailure(
        PublishRequest request,
        string message,
        string[]? warningCodes = null,
        bool? forceAcknowledged = null)
        => new(
            Require(request.Manifest.PackageIdentifier, nameof(request.Manifest.PackageIdentifier)),
            Require(request.Manifest.PackageVersion, nameof(request.Manifest.PackageVersion)),
            Require(request.App.Platform, nameof(request.App.Platform)),
            Require(AppArchitecture.Resolve(request.App), nameof(request.App.Architecture)),
            request.ManifestRepoRelativePath,
            "failed",
            null,
            null,
            message,
            CategoryOutcome: null,
            WarningCodes: warningCodes,
            ForceAcknowledged: forceAcknowledged);

    public static string Serialize(IReadOnlyList<PublishResultEntry> entries)
        => JsonSerializer.Serialize(entries, SerializerOptions);

    public static async Task WriteAsync(
        string resultFilePath,
        IReadOnlyList<PublishResultEntry> entries,
        CancellationToken cancellationToken)
    {
        var fullPath = GetValidatedFullPath(resultFilePath);

        try
        {
            await File.WriteAllTextAsync(fullPath, Serialize(entries), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PublishResultOutputException($"Failed to write publish result file '{resultFilePath}'.", ex);
        }
    }

    public static string GetValidatedFullPath(string resultFilePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resultFilePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PublishResultOutputException($"Result file path '{resultFilePath}' is invalid.", ex);
        }

        if (Directory.Exists(fullPath))
        {
            throw new PublishResultOutputException(
                $"Result file '{resultFilePath}' points to a directory; specify a JSON file path.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new PublishResultOutputException(
                $"Result file directory '{directory ?? resultFilePath}' does not exist.");
        }

        return fullPath;
    }

    public static string ToWireValue(PublishOutcome outcome)
        => outcome switch
        {
            PublishOutcome.Published => "published",
            PublishOutcome.DryRunCompleted => "dry-run",
            PublishOutcome.SkippedDowngrade => "skipped-downgrade",
            PublishOutcome.SkippedPlatformNotSupported => "skipped-platform",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    public static string ToWireValue(ContentUploadOutcome outcome)
        => outcome switch
        {
            ContentUploadOutcome.Uploaded => "uploaded",
            ContentUploadOutcome.SkippedUnchanged => "skipped-unchanged",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };

    /// <summary>
    /// Maps a publish result onto the additive <c>categoryOutcome</c> field. Only a completed
    /// publish (Outcome == Published) with a CategoryPlan reports a value; all other cases
    /// (skipped downloads, unsupported platforms, dry-runs, or failures before the preflight step)
    /// return null.
    /// </summary>
    public static string? ToCategoryWireValue(PublishResult result)
    {
        if (result.Outcome != PublishOutcome.Published || result.CategoryPlan is null)
        {
            return null;
        }

        if (!result.CategoryPlan.Requested)
        {
            return "not-requested";
        }

        return result.CategoryPlan.HasChanges ? "applied" : "unchanged";
    }

    private static string Require(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new PublishResultOutputException($"Cannot write publish result because '{fieldName}' is empty.")
            : value;
}
