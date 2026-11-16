using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;

namespace SyncedNotes.Services;

/// <summary>
/// Exports every note (except the trash) as Markdown files with YAML front matter, grouped into a folder
/// per binder, plus the images they use.
/// </summary>
public sealed partial class ExportService(AppDbContext db)
{
    public async Task<FileStream> CreateZipAsync(string userId, CancellationToken ct = default)
    {
        var binders = await db.Binders.AsNoTracking()
            .Where(b => b.UserId == userId && !b.IsDeleted)
            .ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.UserId == userId && !n.IsDeleted && n.TrashedAt == null)
            .OrderBy(n => n.Title)
            .ToListAsync(ct);
        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => new { a.Id, a.ContentType })
            .ToDictionaryAsync(a => a.Id, a => a.ContentType, ct);

        var stream = new FileStream(
            Path.Combine(Path.GetTempPath(), $"syncednotes-export-{Guid.NewGuid():N}.zip"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

        var usedAttachments = new HashSet<Guid>();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in notes)
            {
                var folder = note.BinderId is { } id && binders.TryGetValue(id, out var binderName)
                    ? SafeName(binderName, "Binder")
                    : "Unfiled";
                if (note.IsArchived) folder = $"Archive/{folder}";

                var baseName = SafeName(note.Title, "Untitled");
                var path = $"notes/{folder}/{baseName}.md";
                for (var i = 2; !usedPaths.Add(path); i++)
                {
                    path = $"notes/{folder}/{baseName} ({i}).md";
                }

                var depth = path.Count(c => c == '/');
                var toRoot = string.Concat(Enumerable.Repeat("../", depth));
                var content = AttachmentUrlRegex().Replace(note.Content, m =>
                {
                    if (!Guid.TryParse(m.Groups[1].Value, out var attachmentId) ||
                        !attachments.TryGetValue(attachmentId, out var type))
                    {
                        return m.Value;
                    }
                    usedAttachments.Add(attachmentId);
                    return $"{toRoot}attachments/{attachmentId}{AttachmentRules.ExtensionFor(type)}";
                });

                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(note.UpdatedAt, 315_532_800_000));
                await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                await writer.WriteAsync(BuildFrontMatter(note, note.BinderId is { } bid && binders.TryGetValue(bid, out var bn) ? bn : null));
                await writer.WriteAsync(content);
            }

            foreach (var attachmentId in usedAttachments)
            {
                var attachment = await db.Attachments.AsNoTracking().FirstAsync(a => a.Id == attachmentId, ct);
                var entry = zip.CreateEntry(
                    $"attachments/{attachment.Id}{AttachmentRules.ExtensionFor(attachment.ContentType)}",
                    CompressionLevel.NoCompression);
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(attachment.Data, ct);
            }

            var readme = zip.CreateEntry("README.txt");
            await using (var writer = new StreamWriter(readme.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(
                    $"SyncedNotes export created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n" +
                    $"{notes.Count} notes (Markdown with YAML front matter) and {usedAttachments.Count} images.\n" +
                    "Archived notes are in notes/Archive. These files can be imported back into SyncedNotes.\n");
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static string BuildFrontMatter(Note note, string? binderName)
    {
        static string Q(string? value) => JsonSerializer.Serialize(value ?? "");
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("title: ").Append(Q(note.Title)).Append('\n');
        if (binderName is not null) sb.Append("binder: ").Append(Q(binderName)).Append('\n');
        if (note.Tags.Count > 0) sb.Append("tags: [").Append(string.Join(", ", note.Tags.Select(Q))).Append("]\n");
        if (note.IsPinned) sb.Append("pinned: true\n");
        if (note.IsArchived) sb.Append("archived: true\n");
        if (note.Color is not null) sb.Append("color: ").Append(note.Color).Append('\n');
        if (note.Icon is not null) sb.Append("icon: ").Append(Q(note.Icon)).Append('\n');
        sb.Append("created: ").Append(Iso(note.CreatedAt)).Append('\n');
        sb.Append("updated: ").Append(Iso(note.UpdatedAt)).Append('\n');
        sb.Append("id: ").Append(note.Id).Append('\n');
        sb.Append("---\n\n");
        return sb.ToString();
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string SafeName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 80) clean = clean[..80].Trim(' ', '.');
        return clean.Length == 0 ? fallback : clean;
    }

    [GeneratedRegex(@"/attachments/([0-9a-fA-F-]{36})")]
    private static partial Regex AttachmentUrlRegex();
}
