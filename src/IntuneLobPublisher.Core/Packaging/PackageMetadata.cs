using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// The shape of <c>package-metadata.json</c>, written next to the generated package by
/// <see cref="IntuneWinPackager"/> (Windows) or the macOS packager and read back by
/// <see cref="PackageMetadataReader"/> during publish. Named types (rather than an anonymous object) so
/// <c>[JsonIgnore(Condition = Never)]</c> can force <see cref="PackageToolMetadata.Version"/> to always be
/// written, even null - a null version (unpinned local tool) is itself the auditability signal and must
/// not be silently dropped by <c>DefaultIgnoreCondition</c>.
/// </summary>
/// <param name="Tool">
/// The external packaging tool used, or null for macOS (which has no IntuneWinAppUtil equivalent).
/// </param>
/// <param name="IntuneWinFile">Windows only: the generated <c>.intunewin</c> file name, relative to this file's directory.</param>
/// <param name="IntuneWinSha256">Windows only: SHA256 of the generated <c>.intunewin</c>. Informational only - the file is encrypted with a random key per run, so this hash is not deterministic.</param>
/// <param name="ContentFile">macOS only: the staged <c>.pkg</c> file's path, relative to this file's directory.</param>
/// <param name="ContentSha256">macOS only: SHA256 of the staged (plaintext) <c>.pkg</c> file.</param>
/// <param name="ContentSize">macOS only: exact byte length of the staged (plaintext) <c>.pkg</c> file.</param>
/// <param name="CliVersion">macOS only: version of the CLI that produced the artifact.</param>
/// <param name="Inspection">macOS only: bounded XAR inspection report for the artifact.</param>
/// <param name="MetadataSchemaVersion">Additive metadata schema version. Windows metadata may omit it.</param>
public sealed record PackageMetadata(
    string PackageIdentifier,
    string? PackageVersion,
    string Platform,
    string Architecture,
    string InputHash,
    PackageToolMetadata? Tool,
    string? IntuneWinFile,
    string? IntuneWinSha256,
    DateTimeOffset GeneratedUtc,
    string? ContentFile = null,
    string? ContentSha256 = null,
    long? ContentSize = null,
    string? CliVersion = null,
    PkgInspectionReport? Inspection = null,
    int? MetadataSchemaVersion = null);

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
