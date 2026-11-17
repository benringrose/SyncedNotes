namespace SyncedNotes.Services;

/// <summary>
/// Image attachments are validated by their content (magic bytes), never by the client-supplied type.
/// SVG is deliberately not accepted because it can contain script.
/// </summary>
public static class AttachmentRules
{
    public static string? DetectImageType(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;

        if (data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)) return "image/gif";
        if (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        if (data[..2].SequenceEqual("BM"u8)) return "image/bmp";
        if (data[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = data[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return "image/avif";
        }
        return null;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/avif" => ".avif",
        _ => ".bin",
    };

    public static string SanitizeFileName(string? name, string contentType)
    {
        var fallback = "image" + ExtensionFor(contentType);
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        clean = Path.GetFileName(clean);
        if (clean.Length > 120) clean = clean[^120..];
        return clean.Length == 0 ? fallback : clean;
    }
}
