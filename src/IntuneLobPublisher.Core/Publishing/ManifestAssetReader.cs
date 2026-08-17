using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// Base64-encoded pre/post-install script content for one macOS <c>AppType: pkg</c> app entry,
/// produced by <see cref="ManifestAssetReader.ReadMacOsScriptsAsync"/> and consumed by
/// <see cref="MacOsAppPayloadMapper.Map"/>. Null when the manifest entry has no <c>Scripts</c> block
/// at all; either field individually is null when that half of the block was omitted.
/// </summary>
public sealed record MacOsAppScripts(string? PreInstall, string? PostInstall);

/// <summary>Reads manifest-referenced files at publish time. Shared by <see cref="WindowsAppPublisher"/> and <see cref="MacOsAppPublisher"/>.</summary>
internal static class ManifestAssetReader
{
    /// <summary>Reads the top-level <c>Icon</c> file, or null when the manifest has none.</summary>
    public static async Task<byte[]?> ReadIconAsync(PublishRequest request, IntunePackageManifest manifest, CancellationToken cancellationToken)
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

    /// <summary>
    /// Reads the macOS <c>Scripts.PreInstall</c> / <c>Scripts.PostInstall</c> files (doc/00-overview.md
    /// §6.13), or null when the app entry has no <c>Scripts</c> block. Each script's line endings are
    /// normalized CRLF/CR -> LF before base64 encoding, since a Windows-checked-out <c>.sh</c> with CRLF
    /// breaks the shebang on macOS; normalization is logged so operators can see it happened.
    /// </summary>
    public static async Task<MacOsAppScripts?> ReadMacOsScriptsAsync(
        PublishRequest request,
        AppManifest app,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (app.Scripts is not { } scripts)
        {
            return null;
        }

        var preInstall = await ReadScriptAsync(request, scripts.PreInstall, "Scripts.PreInstall", logger, cancellationToken)
            .ConfigureAwait(false);
        var postInstall = await ReadScriptAsync(request, scripts.PostInstall, "Scripts.PostInstall", logger, cancellationToken)
            .ConfigureAwait(false);
        return new MacOsAppScripts(preInstall, postInstall);
    }

    private static async Task<string?> ReadScriptAsync(
        PublishRequest request,
        string? scriptPath,
        string fieldName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (scriptPath is null)
        {
            return null;
        }

        var fullPath = PathSafety.ResolveWithin(request.RepositoryRoot, scriptPath, fieldName);
        if (!File.Exists(fullPath))
        {
            throw new ManifestLoadException($"{fieldName} '{scriptPath}' does not exist under '{request.RepositoryRoot}'.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        var normalized = NormalizeLineEndings(text);
        if (!string.Equals(normalized, text, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "{Field} '{ScriptPath}' had CRLF/CR line endings; normalized to LF before encoding for Graph.",
                fieldName, scriptPath);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized));
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
