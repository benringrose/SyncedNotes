using System;

namespace SyncedNotes.CmdPal.Services;

internal enum SyncedNotesErrorKind
{
    HttpError,
    NotConfigured,
    Unauthorized,
    Forbidden,
    NotFound,
    BadRequest,
    Redirect,
    RateLimited,
    ServerError,
    Network,
    Tls,
    Timeout,
    InvalidResponse,
}

/// <summary>
/// A failed SyncedNotes API call. <see cref="Exception.Message"/> is a complete, user-facing
/// sentence that never contains the API token.
/// </summary>
internal sealed class SyncedNotesException : Exception
{
    public SyncedNotesException()
        : this(SyncedNotesErrorKind.HttpError, "The SyncedNotes request failed.")
    {
    }

    public SyncedNotesException(string message)
        : this(SyncedNotesErrorKind.HttpError, message)
    {
    }

    public SyncedNotesException(string message, Exception innerException)
        : this(SyncedNotesErrorKind.HttpError, message, null, innerException)
    {
    }

    public SyncedNotesException(SyncedNotesErrorKind kind, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public SyncedNotesErrorKind Kind { get; }

    public int? StatusCode { get; }

    /// <summary>Gets a value indicating whether the fix is most likely in the extension settings (server URL or API token).</summary>
    public bool IsSettingsProblem => Kind is SyncedNotesErrorKind.NotConfigured
        or SyncedNotesErrorKind.Unauthorized
        or SyncedNotesErrorKind.NotFound
        or SyncedNotesErrorKind.Redirect;
}
