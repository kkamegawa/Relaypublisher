using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Management metadata (doc/00-overview.md 6.1) persisted as JSON in an Intune app's `notes` field.
/// Identifies which manifest owns the app and carries the version/hash values used by the
/// version guard and content-upload skip decision.
/// </summary>
public sealed class ManagementMetadata
{
    /// <summary>Marker value identifying notes written by this tool, as opposed to unrelated admin text.</summary>
    public const string ManagedByValue = "intune-lob-manifest";

    [JsonPropertyName("managedBy")]
    public string ManagedBy { get; init; } = ManagedByValue;

    [JsonPropertyName("packageIdentifier")]
    public required string PackageIdentifier { get; init; }

    [JsonPropertyName("packageVersion")]
    public required string PackageVersion { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("manifestPath")]
    public required string ManifestPath { get; init; }

    [JsonPropertyName("manifestHash")]
    public required string ManifestHash { get; init; }

    [JsonPropertyName("inputHash")]
    public required string InputHash { get; init; }

    [JsonPropertyName("sourceCommit")]
    public required string SourceCommit { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Not documented by the Graph <c>mobileApp.notes</c> property (it is an unbounded Edm.String),
    /// but the Intune admin center's Notes field truncates well below this. Chosen as a conservative
    /// operational ceiling so publish fails fast instead of silently writing truncated metadata.
    /// </summary>
    public const int NotesMaxLength = 10_000;

    /// <summary>
    /// Serializes the metadata to JSON for writing to `notes`.
    /// </summary>
    /// <exception cref="Exceptions.ManagementMetadataTooLargeException">
    /// The serialized JSON exceeds <see cref="NotesMaxLength"/>.
    /// </exception>
    public string Serialize()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        if (json.Length > NotesMaxLength)
        {
            throw new Exceptions.ManagementMetadataTooLargeException(json.Length, NotesMaxLength);
        }

        return json;
    }

    /// <summary>
    /// Attempts to parse management metadata from an Intune app's `notes` value. Returns
    /// <see langword="false"/> for null/blank notes, malformed JSON, or JSON that is not
    /// management metadata written by this tool (missing/mismatched `managedBy`) - all of
    /// which mean "not managed by us", not an error.
    /// </summary>
    public static bool TryParse(string? notes, out ManagementMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(notes))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(notes);
            // ManagedBy defaults to the marker value, so a document that omits it entirely would
            // otherwise deserialize as "managed" - checked explicitly here before deserializing.
            if (!document.RootElement.TryGetProperty("managedBy", out var managedByElement)
                || managedByElement.ValueKind != JsonValueKind.String
                || !string.Equals(managedByElement.GetString(), ManagedByValue, StringComparison.Ordinal))
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ManagementMetadata>(notes);
            if (parsed is null)
            {
                return false;
            }

            metadata = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
