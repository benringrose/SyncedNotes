using Microsoft.AspNetCore.Identity;

namespace SyncedNotes.Data;

/// <summary>
/// The application user. Sign-in is passkey-first (no password is ever set).
/// All timestamps in this application are Unix epoch milliseconds (UTC).
/// </summary>
public class AppUser : IdentityUser
{
    [PersonalData]
    public string? DisplayName { get; set; }

    public long CreatedAt { get; set; }

    /// <summary>Whether the app lock (privacy screen) is enabled for this account.</summary>
    public bool AppLockEnabled { get; set; }

    /// <summary>
    /// Minutes of inactivity before the app locks itself.
    /// 0 = lock as soon as the app is hidden, -1 = never lock automatically (manual only).
    /// </summary>
    public int AppLockTimeoutMinutes { get; set; } = 5;

    /// <summary>Lock whenever the app is freshly launched (new window/tab).</summary>
    public bool AppLockOnLaunch { get; set; } = true;

    /// <summary>Optional unlock PIN, hashed with the Identity password hasher.</summary>
    public string? AppLockPinHash { get; set; }

    /// <summary>Consecutive failed PIN attempts; the PIN is disabled after too many failures.</summary>
    public int AppLockPinFailures { get; set; }
}

/// <summary>A note. Content is stored as Markdown.</summary>
public class Note
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid? BinderId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    /// <summary>When the note was moved to the trash (null = not trashed).</summary>
    public long? TrashedAt { get; set; }
    /// <summary>Manual (drag &amp; drop) ordering key; lower sorts first.</summary>
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    /// <summary>Last time the title or content was modified.</summary>
    public long UpdatedAt { get; set; }
    /// <summary>Per-user change sequence number of the last change to any field.</summary>
    public long Rev { get; set; }
    /// <summary>Change sequence number of the last change to the title or content.</summary>
    public long ContentRev { get; set; }
    /// <summary>Tombstone: the note was permanently deleted (kept so other devices learn about it).</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>A binder groups notes into a category such as Work, Home or Sport.</summary>
public class Binder
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public double SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long Rev { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>An image pasted or dropped into a note. Referenced from Markdown as /attachments/{id}.</summary>
public class Attachment
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public byte[] Data { get; set; } = [];
    public long CreatedAt { get; set; }
}

/// <summary>Personal access token used by integrations such as the PowerToys Command Palette extension.</summary>
public class ApiToken
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Lower-case hex SHA-256 of the full token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>The first few characters of the token, shown in the UI to identify it.</summary>
    public string Prefix { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? LastUsedAt { get; set; }
}

/// <summary>One-time account recovery code (hashed), used when no passkey is available.</summary>
public class RecoveryCode
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? UsedAt { get; set; }
}

/// <summary>Per-user sync state: the change sequence counter and synced preferences.</summary>
public class UserState
{
    public string UserId { get; set; } = "";
    /// <summary>Last allocated change sequence number. Every change gets the next number.</summary>
    public long Seq { get; set; }
    public string? PreferencesJson { get; set; }
    public long PreferencesRev { get; set; }
}
