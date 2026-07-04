using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Staging;

/// <summary>
/// Guards against manifest-supplied paths escaping their allowed root.
/// Every path taken from a manifest (Destination, SetupFile, Source, ScriptFile, Icon)
/// must go through these checks before any file system access.
/// </summary>
public static class PathSafety
{
    /// <summary>Throws when <paramref name="value"/> is absolute or contains traversal segments.</summary>
    public static void EnsureSafeRelativePath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UnsafePathException($"{description} must not be empty.");
        }

        // Check both separator conventions explicitly: Path.IsPathRooted is
        // platform-dependent and would accept "C:\evil" on Unix-like systems.
        if (value.StartsWith('/')
            || value.StartsWith('\\')
            || HasDriveLetterPrefix(value)
            || Path.IsPathRooted(value))
        {
            throw new UnsafePathException($"{description} '{value}' must be a relative path.");
        }

        if (value.Split('/', '\\').Contains(".."))
        {
            throw new UnsafePathException($"{description} '{value}' must not contain path traversal segments.");
        }
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="rootDirectory"/> and
    /// verifies the normalized result stays inside the root.
    /// </summary>
    public static string ResolveWithin(string rootDirectory, string relativePath, string description)
    {
        EnsureSafeRelativePath(relativePath, description);

        var rootFull = Path.GetFullPath(rootDirectory);
        var combined = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootWithSeparator = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsafePathException($"{description} '{relativePath}' escapes the allowed directory after normalization.");
        }

        return combined;
    }

    /// <summary>Throws when <paramref name="value"/> is not usable as a single directory name.</summary>
    public static void EnsureSafeDirectoryName(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UnsafePathException($"{description} must not be empty.");
        }

        if (value is "." or ".."
            || value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            throw new UnsafePathException($"{description} '{value}' contains characters that are not allowed in a directory name.");
        }
    }

    private static bool HasDriveLetterPrefix(string value)
        => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
}
