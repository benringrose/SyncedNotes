using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Sync;

namespace SyncedNotes.Services;

/// <summary>
/// Periodic housekeeping: permanently deletes notes that have been in the trash for 30 days and removes
/// images that no note references any more.
/// </summary>
public sealed partial class CleanupService(IServiceScopeFactory scopes, TimeProvider time, ILogger<CleanupService> logger)
    : BackgroundService
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromDays(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Cleanup failed.");
                }
                await Task.Delay(Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        var now = time.NowMs();
        var trashCutoff = now - (long)TrashRetention.TotalMilliseconds;
        var orphanCutoff = now - (long)OrphanGrace.TotalMilliseconds;

        var userIds = await db.Users.Select(u => u.Id).ToListAsync(ct);
        foreach (var userId in userIds)
        {
            var purged = await sync.WriteAsync(userId, async state =>
            {
                var expired = await db.Notes
                    .Where(n => n.UserId == userId && !n.IsDeleted && n.TrashedAt != null && n.TrashedAt < trashCutoff)
                    .ToListAsync(ct);
                foreach (var note in expired)
                {
                    note.IsDeleted = true;
                    note.Title = "";
                    note.Content = "";
                    note.Tags = [];
                    note.UpdatedAt = now;
                    note.Rev = ++state.Seq;
                }
                return expired.Count;
            }, ct);

            // Images referenced by any remaining note (including archived and trashed ones) are kept.
            var referenced = new HashSet<Guid>();
            var contents = db.Notes.AsNoTracking()
                .Where(n => n.UserId == userId && !n.IsDeleted && n.Content.Contains("/attachments/"))
                .Select(n => n.Content)
                .AsAsyncEnumerable();
            await foreach (var content in contents.WithCancellation(ct))
            {
                foreach (Match match in AttachmentIdRegex().Matches(content))
                {
                    if (Guid.TryParse(match.Groups[1].Value, out var id)) referenced.Add(id);
                }
            }

            var candidates = await db.Attachments
                .Where(a => a.UserId == userId && a.CreatedAt < orphanCutoff)
                .Select(a => a.Id)
                .ToListAsync(ct);
            var orphans = candidates.Where(id => !referenced.Contains(id)).ToList();
            if (orphans.Count > 0)
            {
                await db.Attachments.Where(a => orphans.Contains(a.Id)).ExecuteDeleteAsync(ct);
            }

            if (purged > 0 || orphans.Count > 0)
            {
                logger.LogInformation("Cleanup for {UserId}: {Notes} expired notes deleted, {Images} unused images removed.",
                    userId, purged, orphans.Count);
            }
        }
    }

    [GeneratedRegex(@"/attachments/([0-9a-fA-F-]{36})")]
    private static partial Regex AttachmentIdRegex();
}
