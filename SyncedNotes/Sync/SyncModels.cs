using System.Text.Json;
using SyncedNotes.Data;

namespace SyncedNotes.Sync;

/// <summary>Field names used in <see cref="NoteChange.Fields"/> / <see cref="BinderChange.Fields"/>.</summary>
public static class SyncFields
{
    public const string Title = "title";
    public const string Content = "content";
    public const string BinderId = "binderId";
    public const string Color = "color";
    public const string Icon = "icon";
    public const string Tags = "tags";
    public const string Pinned = "pinned";
    public const string Archived = "archived";
    public const string TrashedAt = "trashedAt";
    public const string SortOrder = "sortOrder";
    public const string Name = "name";

    public static readonly string[] AllNoteFields =
        [Title, Content, BinderId, Color, Icon, Tags, Pinned, Archived, TrashedAt, SortOrder];

    public static readonly string[] AllBinderFields = [Name, Color, Icon, SortOrder];
}

public sealed class NoteDto
{
    public Guid Id { get; set; }
    public Guid? BinderId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public long? TrashedAt { get; set; }
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long Rev { get; set; }
    public bool Deleted { get; set; }

    public static NoteDto From(Note n) => new()
    {
        Id = n.Id,
        BinderId = n.BinderId,
        Title = n.Title,
        Content = n.Content,
        Color = n.Color,
        Icon = n.Icon,
        Tags = n.Tags,
        Pinned = n.IsPinned,
        Archived = n.IsArchived,
        TrashedAt = n.TrashedAt,
        SortOrder = n.SortOrder,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt,
        Rev = n.Rev,
        Deleted = n.IsDeleted,
    };
}

/// <summary>
/// A change to a note pushed by a client. The client sends the full current state of the note plus
/// <see cref="Fields"/>, the names of the fields it changed since <see cref="BaseRev"/> (the last server
/// revision it saw). Only those fields are applied, which lets edits from different devices merge.
/// </summary>
public sealed class NoteChange
{
    public Guid Id { get; set; }
    public long BaseRev { get; set; }
    /// <summary>Changed fields; null means "all fields" (e.g. a brand-new note).</summary>
    public string[]? Fields { get; set; }
    public Guid? BinderId { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public List<string>? Tags { get; set; }
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public long? TrashedAt { get; set; }
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    /// <summary>Permanently delete the note (creates a tombstone).</summary>
    public bool Deleted { get; set; }
}

public sealed class BinderDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long Rev { get; set; }
    public bool Deleted { get; set; }

    public static BinderDto From(Binder b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Color = b.Color,
        Icon = b.Icon,
        SortOrder = b.SortOrder,
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt,
        Rev = b.Rev,
        Deleted = b.IsDeleted,
    };
}

public sealed class BinderChange
{
    public Guid Id { get; set; }
    public long BaseRev { get; set; }
    public string[]? Fields { get; set; }
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

public sealed class SyncPushRequest
{
    /// <summary>The client's current cursor; the response contains every change after it.</summary>
    public long Cursor { get; set; }
    public List<BinderChange>? Binders { get; set; }
    public List<NoteChange>? Notes { get; set; }
    /// <summary>Synced user preferences (theme, sort, view...). Last writer wins.</summary>
    public JsonElement? Preferences { get; set; }
    public int? Limit { get; set; }
}

public static class ChangeStatus
{
    /// <summary>The change was applied.</summary>
    public const string Ok = "ok";
    /// <summary>
    /// The server kept its own (newer) text. If <see cref="ChangeResult.CopyId"/> is set, the client's text was
    /// saved as a separate "conflicted copy" note so nothing is lost.
    /// </summary>
    public const string Conflict = "conflict";
    /// <summary>The change was invalid and was not applied (see <see cref="ChangeResult.Error"/>).</summary>
    public const string Rejected = "rejected";
}

public sealed record ChangeResult(Guid Id, string Status, long Rev, Guid? CopyId = null, string? Error = null);

public sealed class SyncResults
{
    public List<ChangeResult> Binders { get; set; } = [];
    public List<ChangeResult> Notes { get; set; } = [];
}

public sealed class PreferencesDto
{
    public JsonElement Data { get; set; }
    public long Rev { get; set; }
}

public sealed class SyncResponse
{
    public long Cursor { get; set; }
    public bool HasMore { get; set; }
    /// <summary>True when the client must discard its synced state and treat this as a full download.</summary>
    public bool Reset { get; set; }
    public List<BinderDto> Binders { get; set; } = [];
    public List<NoteDto> Notes { get; set; } = [];
    public PreferencesDto? Preferences { get; set; }
    public SyncResults? Results { get; set; }
    public long ServerTime { get; set; }
}
