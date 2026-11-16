using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Services;
using SyncedNotes.Sync;

namespace SyncedNotes.Endpoints;

/// <summary>Sync, image attachments and export for the signed-in (and unlocked) browser session.</summary>
public static class NoteEndpoints
{
    public const long MaxAttachmentBytes = 15 * 1024 * 1024;

    public static void MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireAuthorization()
            .AddEndpointFilter<AntiCsrfHeaderFilter>()
            .AddEndpointFilter<RequireActiveSessionFilter>()
            .WithTags("Notes");

        api.MapGet("/sync", (long? since, int? limit, ClaimsPrincipal principal, SyncService sync, CancellationToken ct) =>
            sync.PullAsync(principal.GetUserId(), since ?? 0, limit, ct));

        api.MapPost("/sync", async (SyncPushRequest request, ClaimsPrincipal principal, SyncService sync, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await sync.PushAsync(principal.GetUserId(), request, ct));
            }
            catch (SyncValidationException ex)
            {
                return ApiProblems.BadRequest("The changes couldn't be synced.", ex.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(64 * 1024 * 1024));

        api.MapPut("/attachments/{id:guid}", UploadAttachmentAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxAttachmentBytes + 1024));

        api.MapGet("/export", async (ClaimsPrincipal principal, ExportService export, CancellationToken ct) =>
        {
            var stream = await export.CreateZipAsync(principal.GetUserId(), ct);
            return Results.File(stream, "application/zip",
                $"SyncedNotes-{DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.zip");
        });

        // Images are referenced from Markdown as /attachments/{id}, so they live outside /api.
        app.MapGet("/attachments/{id:guid}", DownloadAttachmentAsync)
            .RequireAuthorization()
            .AddEndpointFilter<RequireActiveSessionFilter>()
            .WithTags("Notes");
    }

    private static async Task<IResult> UploadAttachmentAsync(
        Guid id, HttpRequest request, ClaimsPrincipal principal, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var existing = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.UserId, a.Size })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            // Uploads are idempotent: the client retries until it sees success.
            return existing.UserId == userId
                ? Results.Ok(new { id, size = existing.Size })
                : ApiProblems.Conflict("That attachment id is already in use.");
        }

        if (request.ContentLength > MaxAttachmentBytes)
        {
            return ApiProblems.Problem(StatusCodes.Status413PayloadTooLarge, "Images must be 15 MB or smaller.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxAttachmentBytes)
            {
                return ApiProblems.Problem(StatusCodes.Status413PayloadTooLarge, "Images must be 15 MB or smaller.");
            }
            buffer.Write(chunk, 0, read);
        }

        var data = buffer.ToArray();
        var contentType = AttachmentRules.DetectImageType(data);
        if (contentType is null)
        {
            return ApiProblems.Problem(StatusCodes.Status415UnsupportedMediaType,
                "Unsupported image.", "PNG, JPEG, GIF, WebP, BMP and AVIF images are supported.");
        }

        var fileName = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString());
        db.Attachments.Add(new Attachment
        {
            Id = id,
            UserId = userId,
            FileName = AttachmentRules.SanitizeFileName(fileName, contentType),
            ContentType = contentType,
            Size = data.Length,
            Data = data,
            CreatedAt = time.NowMs(),
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id, size = data.Length, contentType });
    }

    private static async Task<IResult> DownloadAttachmentAsync(
        Guid id, HttpContext context, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var attachment = await db.Attachments.AsNoTracking()
            .Where(a => a.Id == id && a.UserId == userId)
            .Select(a => new { a.Data, a.ContentType, a.FileName })
            .FirstOrDefaultAsync(ct);
        if (attachment is null)
        {
            return ApiProblems.NotFound("Image not found.");
        }

        var headers = context.Response.Headers;
        // Attachments never change once uploaded, so they can be cached forever (privately).
        headers.CacheControl = "private, max-age=31536000, immutable";
        headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox";
        headers.ContentDisposition = $"inline; filename=\"{Uri.EscapeDataString(attachment.FileName)}\"";
        return Results.File(attachment.Data, attachment.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{id:N}\""));
    }
}
