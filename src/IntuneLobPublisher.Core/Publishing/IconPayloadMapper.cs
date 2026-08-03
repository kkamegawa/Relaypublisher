using IntuneLobPublisher.Core.Exceptions;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>Maps a manifest <c>Icon</c> to the Graph <c>largeIcon</c> shape. Shared by Windows and macOS payload mappers.</summary>
internal static class IconPayloadMapper
{
    public static MimeContentPayload? Map(string? iconPath, byte[]? iconBytes)
    {
        if (iconBytes is null || iconPath is null)
        {
            return null;
        }

        return new MimeContentPayload
        {
            Type = ResolveMimeType(iconPath),
            Value = Convert.ToBase64String(iconBytes),
        };
    }

    /// <exception cref="UnsupportedIconFormatException">The extension has no known Graph largeIcon MIME type mapping.</exception>
    private static string ResolveMimeType(string iconPath) => Path.GetExtension(iconPath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => throw new UnsupportedIconFormatException(iconPath),
    };
}
