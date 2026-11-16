using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Sync;

namespace SyncedNotes.Endpoints;

public sealed record QuickNoteRequest(string? Title, string? Content, List<string>? Tags, string? Binder, bool? Pinned);

/// <summary>
/// Token-authenticated API for integrations such as the PowerToys Command Palette extension.
/// Authenticate with <c>Authorization: Bearer snp_...</c> (create a token in Settings → Integrations).
/// </summary>
public static partial class QuickEndpoints
{
    public static void MapQuickEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quick")
            .RequireAuthorization(ApiTokenDefaults.Policy)
            .RequireRateLimiting(RateLimits.Quick)
            .WithTags("Integrations");

        group.MapPost("/notes", CreateNoteAsync);
        group.MapGet("/notes", SearchNotesAsync);
        group.MapGet("/binders", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var binders = await db.Binders.AsNoTracking()
                .Where(b => b.UserId == userId && !b.IsDeleted)
                .OrderBy(b => b.SortOrder)
                .Select(b => new { b.Id, b.Name })
                .ToListAsync(ct);
            return Results.Ok(binders);
        });
    }

    private static async Task<IResult> CreateNoteAsync(
        QuickNoteRequest request,
        HttpRequest http,
        ClaimsPrincipal principal,
        AppDbContext db,
        SyncService sync,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var content = (request.Content ?? "").Replace("\r\n", "\n");
        if (content.Length > NoteLimits.MaxContentLength)
        {
            return ApiProblems.BadRequest("The note is too large.");
        }

        var title = NoteRules.NormalizeTitle(request.Title);
        if (title.Length == 0) title = NoteRules.DeriveTitle(content);
        if (title.Length == 0 && content.Trim().Length == 0)
        {
            return ApiProblems.BadRequest("A note needs a title or some content.");
        }
        if (title.Length == 0) title = "Quick note";

        Guid? binderId = null;
        if (!string.IsNullOrWhiteSpace(request.Binder))
        {
            var binders = await db.Binders.AsNoTracking()
                .Where(b => b.UserId == userId && !b.IsDeleted)
                .Select(b => new { b.Id, b.Name })
                .ToListAsync(ct);
            binderId = binders.FirstOrDefault(b => string.Equals(b.Name, request.Binder.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;
        }

        var now = time.NowMs();
        var note = await sync.WriteAsync(userId, async state =>
        {
            var minSort = await db.Notes.Where(n => n.UserId == userId && !n.IsDeleted)
                .MinAsync(n => (double?)n.SortOrder, ct) ?? 1;
            var created = new Note
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BinderId = binderId,
                Title = title,
                Content = content,
                Tags = NoteRules.NormalizeTags(request.Tags),
                IsPinned = request.Pinned ?? false,
                SortOrder = minSort - 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            created.Rev = created.ContentRev = ++state.Seq;
            db.Notes.Add(created);
            return created;
        }, ct);

        var url = NoteUrl(http, note.Id);
        return Results.Created(url, new { id = note.Id, title = note.Title, url });
    }

    private static async Task<IResult> SearchNotesAsync(
        string? q, int? limit, HttpRequest http, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var take = Math.Clamp(limit ?? 20, 1, 50);
        var query = db.Notes.AsNoTracking().Where(n => n.UserId == userId && !n.IsDeleted && !n.IsArchived && n.TrashedAt == null);

        var text = q?.Trim() ?? "";
        if (text.Length > 0)
        {
            var pattern = $"%{EscapeLike(text)}%";
            query = query.Where(n =>
                EF.Functions.Like(n.Title, pattern, "\\") ||
                EF.Functions.Like(n.Content, pattern, "\\") ||
                n.Tags.Any(t => EF.Functions.Like(t, pattern, "\\")));
        }

        var notes = await query
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt)
            .Take(take)
            .Select(n => new { n.Id, n.Title, Excerpt = n.Content.Substring(0, 600), n.BinderId, n.Tags, n.IsPinned, n.UpdatedAt })
            .ToListAsync(ct);

        var binderIds = notes.Where(n => n.BinderId != null).Select(n => n.BinderId!.Value).Distinct().ToList();
        var binderNames = await db.Binders.AsNoTracking()
            .Where(b => b.UserId == userId && binderIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        return Results.Ok(notes.Select(n => new
        {
            id = n.Id,
            title = string.IsNullOrEmpty(n.Title) ? "Untitled" : n.Title,
            snippet = Snippet(n.Excerpt),
            binder = n.BinderId is { } b && binderNames.TryGetValue(b, out var name) ? name : null,
            tags = n.Tags,
            pinned = n.IsPinned,
            updatedAt = n.UpdatedAt,
            url = NoteUrl(http, n.Id),
        }));
    }

    private static string NoteUrl(HttpRequest request, Guid id) => $"{request.BaseUrl()}/#/note/{id}";

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>A short plain-text preview of Markdown content.</summary>
    public static string Snippet(string? markdown, int length = 160)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var text = FenceRegex().Replace(markdown, " ");
        text = ImageRegex().Replace(text, " ");
        text = LinkRegex().Replace(text, "$1");
        text = MarkupRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length > length ? text[..length].TrimEnd() + "…" : text;
    }

    [GeneratedRegex(@"```")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(^|\s)(#{1,6}|>|[-*+]\s\[[ xX]\]|[-*+]|\d+\.)\s|[*_~`|]")]
    private static partial Regex MarkupRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
