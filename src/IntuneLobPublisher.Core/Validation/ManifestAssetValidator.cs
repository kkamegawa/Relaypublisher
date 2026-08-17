using System.Text;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Validation;

/// <summary>
/// Validates manifest-referenced files that require repository-root file system access
/// (existence, size, content shape), which <see cref="ManifestValidator"/> cannot check on its
/// own since it has no repository root. Covers <see cref="IntunePackageManifest.Icon"/>
/// (issue #63: format is checked by <see cref="ManifestValidator"/>, existence and size here) and
/// macOS <see cref="MacOsScriptsManifest"/> pre/post-install scripts (issue #86: existence,
/// character count, BOM and shebang here; path shape and extension are checked by
/// <see cref="ManifestValidator"/>), so a bad file fails before any Graph call rather than during
/// publish.
/// </summary>
public static class ManifestAssetValidator
{
    private const char Utf8BomChar = '\uFEFF';

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Returns error strings (empty when valid) for the given manifest's file-backed assets.</summary>
    public static IReadOnlyList<string> Validate(IntunePackageManifest manifest, string repositoryRoot)
    {
        var errors = new List<string>();

        if (manifest.Icon is { } icon)
        {
            errors.AddRange(ValidateIcon(icon, repositoryRoot));
        }

        foreach (var app in manifest.Apps)
        {
            if (app.Scripts is not { } scripts)
            {
                continue;
            }

            if (scripts.PreInstall is { } preInstall)
            {
                errors.AddRange(ValidateScript(preInstall, repositoryRoot, "Scripts.PreInstall"));
            }

            if (scripts.PostInstall is { } postInstall)
            {
                errors.AddRange(ValidateScript(postInstall, repositoryRoot, "Scripts.PostInstall"));
            }
        }

        return errors;
    }

    private static IReadOnlyList<string> ValidateIcon(string icon, string repositoryRoot)
    {
        // Path safety (traversal, absolute paths) is already checked by ManifestValidator before this
        // runs; ResolveWithin re-validates defensively rather than trusting an already-loaded manifest.
        string iconPath;
        try
        {
            iconPath = PathSafety.ResolveWithin(repositoryRoot, icon, "Icon");
        }
        catch (UnsafePathException ex)
        {
            return [ex.Message];
        }

        if (!File.Exists(iconPath))
        {
            return [$"Icon '{icon}' does not exist under '{repositoryRoot}'."];
        }

        var sizeBytes = new FileInfo(iconPath).Length;
        if (sizeBytes > ManifestValues.MaxIconBytes)
        {
            return [$"Icon '{icon}' is {sizeBytes} bytes, which exceeds the maximum of {ManifestValues.MaxIconBytes} bytes."];
        }

        return [];
    }

    private static IReadOnlyList<string> ValidateScript(string scriptPath, string repositoryRoot, string fieldName)
    {
        string fullPath;
        try
        {
            fullPath = PathSafety.ResolveWithin(repositoryRoot, scriptPath, fieldName);
        }
        catch (UnsafePathException ex)
        {
            return [ex.Message];
        }

        if (!File.Exists(fullPath))
        {
            return [$"{fieldName} '{scriptPath}' does not exist under '{repositoryRoot}'."];
        }

        var bytes = File.ReadAllBytes(fullPath);
        var errors = new List<string>();

        // A BOM before the shebang stops macOS from launching the script at all, so it is
        // rejected outright rather than silently stripped.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, Utf8Bom.Length)).SequenceEqual(Utf8Bom))
        {
            errors.Add($"{fieldName} '{scriptPath}' must not have a UTF-8 byte order mark (BOM).");
        }

        var text = Encoding.UTF8.GetString(bytes);

        // Graph's documented limit is on the un-encoded script text (doc/01-manifest-schema.md §5.4.2).
        if (text.Length >= ManifestValues.MaxMacOsAppScriptChars)
        {
            errors.Add(
                $"{fieldName} '{scriptPath}' is {text.Length} characters, which meets or exceeds the maximum of "
                + $"{ManifestValues.MaxMacOsAppScriptChars} characters.");
        }

        if (!text.TrimStart(Utf8BomChar).StartsWith("#!", StringComparison.Ordinal))
        {
            errors.Add($"{fieldName} '{scriptPath}' must start with a shebang ('#!').");
        }

        return errors;
    }
}
