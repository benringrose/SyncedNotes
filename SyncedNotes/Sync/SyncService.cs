using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;

namespace SyncedNotes.Sync;

public sealed class SyncValidationException(string message) : Exception(message);

/// <summary>
/// Offline-first sync. Every change a user makes is stamped with the next number from that user's change
/// sequence (<see cref="UserState.Seq"/>). Clients pull "everything after cursor N" and push their local
/// changes along with the revision they were based on, which lets the server merge field by field and
/// detect real text conflicts (both sides edited the text), saving the losing side as a separate copy.
/// </summary>
public sealed class SyncService(AppDbContext db, TimeProvider time)
{
    public const int DefaultPullLimit = 500;
    public const int MaxPullLimit = 2000;
    /// <summary>Approximate maximum size of one pull response; larger change sets are paged.</summary>
    private const long PullByteBudget = 8_000_000;

    public async Task<UserState> GetOrCreateStateAsync(string userId, CancellationToken ct = default)
    {
        var state = await db.UserStates.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state is null)
        {
            state = new UserState { UserId = userId };
            db.UserStates.Add(state);
        }
        return state;
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a write transaction with the user's sync state loaded, so it can
    /// allocate revisions with <c>++state.Seq</c>. SQLite transactions are IMMEDIATE, so writers are serialised
    /// and revisions always commit in order.
    /// </summary>
    public async Task<T> WriteAsync<T>(string userId, Func<UserState, Task<T>> action, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await GetOrCreateStateAsync(userId, ct);
        var result = await action(state);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<SyncResponse> PullAsync(string userId, long since, int? limit, CancellationToken ct = default)
    {
        var state = await db.UserStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return await PullCoreAsync(userId, since, limit, state, ct);
    }

    public async Task<SyncResponse> PushAsync(string userId, SyncPushRequest request, CancellationToken ct = default)
    {
        var binderChanges = request.Binders ?? [];
        var noteChanges = request.Notes ?? [];
        if (binderChanges.Count + noteChanges.Count > NoteLimits.MaxChangesPerPush)
        {
            throw new SyncValidationException($"A push may contain at most {NoteLimits.MaxChangesPerPush} changes.");
        }

        var results = new SyncResults();
        var now = time.NowMs();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await GetOrCreateStateAsync(userId, ct);

        // 1. Binders first, so notes in the same push can reference newly created binders.
        var binderIds = binderChanges.Select(b => b.Id).Distinct().ToList();
        var binders = await db.Binders.Where(b => binderIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var deletedBinderIds = new List<Guid>();
        foreach (var change in binderChanges)
        {
            results.Binders.Add(ApplyBinder(userId, state, change, binders, deletedBinderIds, now));
        }
        await db.SaveChangesAsync(ct);

        var liveBinderIds = (await db.Binders
            .Where(b => b.UserId == userId && !b.IsDeleted)
            .Select(b => b.Id)
            .ToListAsync(ct)).ToHashSet();

        // 2. Notes.
        var noteIds = noteChanges.Select(n => n.Id).Distinct().ToList();
        var notes = await db.Notes.Where(n => noteIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        foreach (var change in noteChanges)
        {
            results.Notes.Add(ApplyNote(userId, state, change, notes, liveBinderIds, now));
        }

        // 3. Notes left in a deleted binder become unfiled (on every device).
        if (deletedBinderIds.Count > 0)
        {
            var orphans = await db.Notes
                .Where(n => n.UserId == userId && n.BinderId != null && deletedBinderIds.Contains(n.BinderId.Value))
                .ToListAsync(ct);
            foreach (var note in orphans)
            {
                note.BinderId = null;
                note.Rev = ++state.Seq;
            }
        }

        // 4. Preferences (last writer wins).
        if (request.Preferences is { ValueKind: JsonValueKind.Object } preferences)
        {
            var json = preferences.GetRawText();
            if (json.Length > NoteLimits.MaxPreferencesLength)
            {
                throw new SyncValidationException("Preferences are too large.");
            }
            state.PreferencesJson = json;
            state.PreferencesRev = ++state.Seq;
        }

        await db.SaveChangesAsync(ct);

        // 5. Return everything the client hasn't seen yet (including the effects of this push).
        var response = await PullCoreAsync(userId, request.Cursor, request.Limit, state, ct);
        await tx.CommitAsync(ct);
        response.Results = results;
        return response;
    }

    private async Task<SyncResponse> PullCoreAsync(string userId, long since, int? limit, UserState? state, CancellationToken ct)
    {
        // Read the sequence high-water mark FIRST and only return rows at or below it. Anything committed after
        // this point has a higher revision and will be picked up by the next pull, so nothing can be skipped.
        var max = state?.Seq ?? 0;
        var reset = false;
        if (since < 0 || since > max)
        {
            // The client is ahead of the server (for example the database was restored from a backup).
            since = 0;
            reset = true;
        }

        var take = Math.Clamp(limit ?? DefaultPullLimit, 1, MaxPullLimit);
        var includeTombstones = since > 0;

        var binders = await db.Binders.AsNoTracking()
            .Where(b => b.UserId == userId && b.Rev > since && b.Rev <= max && (includeTombstones || !b.IsDeleted))
            .OrderBy(b => b.Rev)
            .ToListAsync(ct);

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.UserId == userId && n.Rev > since && n.Rev <= max && (includeTombstones || !n.IsDeleted))
            .OrderBy(n => n.Rev)
            .Take(take + 1)
            .ToListAsync(ct);

        var hasMore = notes.Count > take;
        if (hasMore)
        {
            notes.RemoveAt(notes.Count - 1);
        }

        // Keep each response to a reasonable size; big notes are paged across several pulls.
        long bytes = 0;
        var keep = 0;
        foreach (var note in notes)
        {
            bytes += (note.Content.Length + note.Title.Length) * 2L + 256;
            keep++;
            if (bytes > PullByteBudget) break;
        }
        if (keep < notes.Count)
        {
            notes.RemoveRange(keep, notes.Count - keep);
            hasMore = true;
        }

        PreferencesDto? preferences = null;
        if (state?.PreferencesJson is { } json && state.PreferencesRev > since)
        {
            using var doc = JsonDocument.Parse(json);
            preferences = new PreferencesDto { Data = doc.RootElement.Clone(), Rev = state.PreferencesRev };
        }

        return new SyncResponse
        {
            Cursor = hasMore ? notes[^1].Rev : max,
            HasMore = hasMore,
            Reset = reset,
            Binders = binders.Select(BinderDto.From).ToList(),
            Notes = notes.Select(NoteDto.From).ToList(),
            Preferences = preferences,
            ServerTime = time.NowMs(),
        };
    }

    private ChangeResult ApplyBinder(
        string userId, UserState state, BinderChange change, Dictionary<Guid, Binder> binders, List<Guid> deleted, long now)
    {
        var fields = FieldSet(change.Fields, SyncFields.AllBinderFields);
        var name = NoteRules.NormalizeBinderName(change.Name);

        if (!binders.TryGetValue(change.Id, out var binder))
        {
            if (change.Deleted) return new ChangeResult(change.Id, ChangeStatus.Ok, 0);
            if (name.Length == 0) return new ChangeResult(change.Id, ChangeStatus.Rejected, 0, Error: "A binder needs a name.");

            binder = new Binder
            {
                Id = change.Id,
                UserId = userId,
                Name = name,
                Color = NoteRules.NormalizeColor(change.Color),
                Icon = NoteRules.NormalizeIcon(change.Icon),
                SortOrder = NoteRules.NormalizeSortOrder(change.SortOrder),
                CreatedAt = NoteRules.ClampTime(change.CreatedAt, now),
                UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now),
                Rev = ++state.Seq,
            };
            db.Binders.Add(binder);
            binders[binder.Id] = binder;
            return new ChangeResult(binder.Id, ChangeStatus.Ok, binder.Rev);
        }

        if (binder.UserId != userId)
        {
            return new ChangeResult(change.Id, ChangeStatus.Rejected, 0, Error: "Not found.");
        }

        if (change.Deleted)
        {
            if (!binder.IsDeleted)
            {
                binder.IsDeleted = true;
                binder.UpdatedAt = now;
                binder.Rev = ++state.Seq;
                deleted.Add(binder.Id);
            }
            return new ChangeResult(binder.Id, ChangeStatus.Ok, binder.Rev);
        }

        if (binder.IsDeleted)
        {
            // Deleted on another device but changed here: bring it back with this device's values.
            binder.IsDeleted = false;
            fields = [.. SyncFields.AllBinderFields];
        }

        if (fields.Contains(SyncFields.Name) && name.Length > 0) binder.Name = name;
        if (fields.Contains(SyncFields.Color)) binder.Color = NoteRules.NormalizeColor(change.Color);
        if (fields.Contains(SyncFields.Icon)) binder.Icon = NoteRules.NormalizeIcon(change.Icon);
        if (fields.Contains(SyncFields.SortOrder)) binder.SortOrder = NoteRules.NormalizeSortOrder(change.SortOrder);
        binder.UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now);
        binder.Rev = ++state.Seq;
        return new ChangeResult(binder.Id, ChangeStatus.Ok, binder.Rev);
    }

    private ChangeResult ApplyNote(
        string userId, UserState state, NoteChange change, Dictionary<Guid, Note> notes, HashSet<Guid> liveBinderIds, long now)
    {
        var content = change.Content ?? "";
        if (content.Length > NoteLimits.MaxContentLength)
        {
            return new ChangeResult(change.Id, ChangeStatus.Rejected, 0, Error: "The note is too large.");
        }

        var fields = FieldSet(change.Fields, SyncFields.AllNoteFields);
        var title = NoteRules.NormalizeTitle(change.Title);
        var tags = NoteRules.NormalizeTags(change.Tags);
        var color = NoteRules.NormalizeColor(change.Color);
        var icon = NoteRules.NormalizeIcon(change.Icon);
        var sortOrder = NoteRules.NormalizeSortOrder(change.SortOrder);
        var trashedAt = NoteRules.ClampOptionalTime(change.TrashedAt, now);
        Guid? binderId = change.BinderId is { } b && liveBinderIds.Contains(b) ? b : null;

        if (!notes.TryGetValue(change.Id, out var note))
        {
            // Created and permanently deleted before it ever reached the server: nothing to do.
            if (change.Deleted) return new ChangeResult(change.Id, ChangeStatus.Ok, 0);

            note = new Note
            {
                Id = change.Id,
                UserId = userId,
                BinderId = binderId,
                Title = title,
                Content = content,
                Color = color,
                Icon = icon,
                Tags = tags,
                IsPinned = change.Pinned,
                IsArchived = change.Archived,
                TrashedAt = trashedAt,
                SortOrder = sortOrder,
                CreatedAt = NoteRules.ClampTime(change.CreatedAt, now),
                UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now),
            };
            note.Rev = note.ContentRev = ++state.Seq;
            db.Notes.Add(note);
            notes[note.Id] = note;
            return new ChangeResult(note.Id, ChangeStatus.Ok, note.Rev);
        }

        // Never touch another user's data, even if a client somehow guessed an id.
        if (note.UserId != userId)
        {
            return new ChangeResult(change.Id, ChangeStatus.Rejected, 0, Error: "Not found.");
        }

        if (note.IsDeleted)
        {
            if (change.Deleted) return new ChangeResult(note.Id, ChangeStatus.Ok, note.Rev);

            // Permanently deleted elsewhere but edited here: keep this device's edits.
            note.IsDeleted = false;
            note.Title = title;
            note.Content = content;
            note.BinderId = binderId;
            note.Color = color;
            note.Icon = icon;
            note.Tags = tags;
            note.IsPinned = change.Pinned;
            note.IsArchived = change.Archived;
            note.TrashedAt = trashedAt;
            note.SortOrder = sortOrder;
            note.UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now);
            note.Rev = note.ContentRev = ++state.Seq;
            return new ChangeResult(note.Id, ChangeStatus.Ok, note.Rev);
        }

        var textChangedOnServer = note.ContentRev > change.BaseRev;

        if (change.Deleted)
        {
            // Don't destroy text that this device has never seen. Bump the revision so the kept version is
            // included in this response's pull even if the device's cursor is already past the last change.
            if (textChangedOnServer)
            {
                note.Rev = ++state.Seq;
                return new ChangeResult(note.Id, ChangeStatus.Conflict, note.Rev);
            }

            note.IsDeleted = true;
            note.Title = "";
            note.Content = "";
            note.Tags = [];
            note.Color = null;
            note.Icon = null;
            note.BinderId = null;
            note.IsPinned = false;
            note.UpdatedAt = now;
            note.Rev = ++state.Seq;
            return new ChangeResult(note.Id, ChangeStatus.Ok, note.Rev);
        }

        Guid? copyId = null;
        var clientChangedText = fields.Contains(SyncFields.Title) || fields.Contains(SyncFields.Content);
        var revision = ++state.Seq;
        if (clientChangedText && (note.Title != title || note.Content != content))
        {
            if (textChangedOnServer)
            {
                // Both devices edited the text. Keep the server's version and save this device's version as a
                // separate note so no writing is ever lost.
                var copy = new Note
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    BinderId = fields.Contains(SyncFields.BinderId) ? binderId : note.BinderId,
                    Title = NoteRules.ConflictTitle(title, now),
                    Content = content,
                    Color = color,
                    Icon = icon,
                    Tags = tags,
                    IsArchived = note.IsArchived,
                    SortOrder = note.SortOrder - 0.5,
                    CreatedAt = now,
                    UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now),
                };
                copy.Rev = copy.ContentRev = ++state.Seq;
                db.Notes.Add(copy);
                notes[copy.Id] = copy;
                copyId = copy.Id;
            }
            else
            {
                note.Title = title;
                note.Content = content;
                note.UpdatedAt = NoteRules.ClampTime(change.UpdatedAt, now);
                note.ContentRev = revision;
            }
        }

        // Everything else merges field by field; the most recent writer wins.
        if (fields.Contains(SyncFields.BinderId)) note.BinderId = binderId;
        if (fields.Contains(SyncFields.Color)) note.Color = color;
        if (fields.Contains(SyncFields.Icon)) note.Icon = icon;
        if (fields.Contains(SyncFields.Tags)) note.Tags = tags;
        if (fields.Contains(SyncFields.Pinned)) note.IsPinned = change.Pinned;
        if (fields.Contains(SyncFields.Archived)) note.IsArchived = change.Archived;
        if (fields.Contains(SyncFields.TrashedAt)) note.TrashedAt = trashedAt;
        if (fields.Contains(SyncFields.SortOrder)) note.SortOrder = sortOrder;
        note.Rev = revision;

        return new ChangeResult(note.Id, copyId is null ? ChangeStatus.Ok : ChangeStatus.Conflict, note.Rev, copyId);
    }

    private static HashSet<string> FieldSet(string[]? fields, string[] all) =>
        new(fields ?? all, StringComparer.OrdinalIgnoreCase);
}
