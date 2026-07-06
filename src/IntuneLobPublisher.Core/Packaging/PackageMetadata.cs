using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// The shape of <c>package-metadata.json</c>, written next to the generated <c>.intunewin</c> by
/// <see cref="IntuneWinPackager"/> and read back by <see cref="PackageMetadataReader"/> during publish.
/// Named types (rather than an anonymous object) so <c>[JsonIgnore(Condition = Never)]</c> can force
/// <see cref="PackageToolMetadata.Version"/> to always be written, even null - a null version
/// (unpinned local tool) is itself the auditability signal and must not be silently dropped by
/// <c>DefaultIgnoreCondition</c>.
/// </summary>
public sealed record PackageMetadata(
    string PackageIdentifier,
    string? PackageVersion,
    string Platform,
    string Architecture,
    string InputHash,
    PackageToolMetadata Tool,
    string IntuneWinFile,
    string IntuneWinSha256,
    DateTimeOffset GeneratedUtc);

public sealed record PackageToolMetadata(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version,
    string Sha256);

/// <summary>Serializer settings and file name shared by the package-metadata.json writer and reader.</summary>
public static class PackageMetadataJson
{
    public const string FileName = "package-metadata.json";

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
