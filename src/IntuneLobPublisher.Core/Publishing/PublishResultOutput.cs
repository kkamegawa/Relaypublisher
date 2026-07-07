using System.Text.Json;
using System.Text.Json.Serialization;
using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Machine-readable publish result for one manifest app entry.</summary>
public sealed record PublishResultEntry(
    [property: JsonPropertyName("packageIdentifier")] string PackageIdentifier,
    [property: JsonPropertyName("packageVersion")] string PackageVersion,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("appId")] string? AppId,
    [property: JsonPropertyName("contentOutcome")] string? ContentOutcome,
    [property: JsonPropertyName("skipReason")] string? SkipReason);

/// <summary>Creates and writes stable JSON output for CI integrations.</summary>
public static class PublishResultOutput
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static PublishResultEntry FromResult(PublishRequest request, PublishResult result)
        => new(
            Require(request.Manifest.PackageIdentifier, nameof(request.Manifest.PackageIdentifier)),
            Require(request.Manifest.PackageVersion, nameof(request.Manifest.PackageVersion)),
            Require(request.App.Platform, nameof(request.App.Platform)),
            Require(request.App.Architecture, nameof(request.App.Architecture)),
            request.ManifestRepoRelativePath,
            ToWireValue(result.Outcome),
            result.AppId,
            result.ContentOutcome is null ? null : ToWireValue(result.ContentOutcome.Value),
            result.SkipReason);

    public static PublishResultEntry FromFailure(PublishRequest request, string message)
        => new(
            Require(request.Manifest.PackageIdentifier, nameof(request.Manifest.PackageIdentifier)),
            Require(request.Manifest.PackageVersion, nameof(request.Manifest.PackageVersion)),
            Require(request.App.Platform, nameof(request.App.Platform)),
            Require(request.App.Architecture, nameof(request.App.Architecture)),
            request.ManifestRepoRelativePath,
            "failed",
            null,
            null,
            message);

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

    private static string Require(string? value, string fieldName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new PublishResultOutputException($"Cannot write publish result because '{fieldName}' is empty.")
            : value;
}
