using System.Collections.Generic;
using SyncedNotes.CmdPal.Helpers;

namespace SyncedNotes.CmdPal.Services;

/// <summary>A note that is about to be created.</summary>
internal sealed record NoteDraft(
    string? Title,
    string Content,
    IReadOnlyList<string>? Tags = null,
    string? Binder = null,
    bool Pinned = false)
{
    public const int MaxTitleLength = 200;

    /// <summary>Gets a value indicating whether there is nothing to save (the API needs a title or content).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Content);

    /// <summary>Gets the title the note will most likely get (the server derives it from the first line when no title is given).</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? TextHelpers.FirstLine(Content) : Title.Trim();

    /// <summary>A quick note typed into the palette: the whole text becomes the content, the server picks the title.</summary>
    public static NoteDraft FromText(string text) => new(null, text.Trim());

    public CreateNoteRequest ToRequest()
    {
        var title = string.IsNullOrWhiteSpace(Title) ? null : TextHelpers.SingleLine(Title);
        if (title is { Length: > MaxTitleLength })
        {
            title = title[..MaxTitleLength];
        }

        return new CreateNoteRequest
        {
            Title = title,
            Content = Content?.TrimEnd() ?? string.Empty,
            Tags = Tags is { Count: > 0 } ? [.. Tags] : null,
            Binder = string.IsNullOrWhiteSpace(Binder) ? null : Binder.Trim(),
            Pinned = Pinned ? true : null,
        };
    }
}

/// <summary>A note that was created on the server.</summary>
/// <param name="Id">The note id.</param>
/// <param name="Title">The final title.</param>
/// <param name="Url">An http(s) link to the note in the web app, or empty if none could be determined.</param>
internal sealed record SavedNote(string Id, string Title, string Url);
