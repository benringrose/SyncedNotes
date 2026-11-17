using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Commands;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Pages;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Fallbacks;

/// <summary>
/// Fallback item on Command Palette's home page: whatever you type is offered as
/// "Add note to SyncedNotes: …". UpdateQuery runs on every keystroke, so it only updates
/// the row; the network call happens when the item is invoked.
/// Command Palette hides fallback rows whose title is empty.
/// </summary>
internal sealed partial class AddNoteFallbackItem : FallbackCommandItem
{
    private const string FallbackId = "SyncedNotes.CmdPal.Fallback.AddNote";

    private readonly SyncedNotesService _service;

    public AddNoteFallbackItem(SyncedNotesService service)
        : base("Add note to SyncedNotes", FallbackId)
    {
        _service = service;
        Icon = Icons.NewNote;
        Title = string.Empty; // hidden until something is typed
    }

    public override void UpdateQuery(string query)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            Hide();
            return;
        }

        if (!_service.Settings.TryGetConnection(out _, out var problem))
        {
            Command = _service.SettingsPage;
            Title = $"Set up SyncedNotes to save “{TextHelpers.Ellipsize(text, 50)}” as a note";
            Subtitle = problem;
            MoreCommands = [];
            return;
        }

        var draft = NoteDraft.FromText(text);

        // "Keep open" would leave the same text in the search box (and invite a duplicate),
        // so the fallback always closes the palette unless the user asked to open the note.
        var behavior = _service.Settings.AfterSave == AfterSaveBehavior.OpenNote
            ? AfterSaveBehavior.OpenNote
            : AfterSaveBehavior.Close;

        Command = new SaveNoteCommand(_service, draft, behavior) { Id = FallbackId + ".Save", Name = "Add note" };
        Title = $"Add note to SyncedNotes: {TextHelpers.Ellipsize(text, 80)}";
        Subtitle = "Saves the text as a new note (the first line becomes the title)";
        MoreCommands =
        [
            new CommandContextItem(new SaveNoteCommand(_service, draft, AfterSaveBehavior.OpenNote))
            {
                Title = "Add note and open it",
            },
            new CommandContextItem(new NewNoteFormPage(_service, draft.Content))
            {
                Title = "Continue in the full note form…",
                Icon = Icons.Form,
            },
        ];
    }

    private void Hide()
    {
        // CommandItem.Title falls back to the command's Name, so swap in a nameless command too.
        Command = new NoOpCommand();
        Title = string.Empty;
        Subtitle = string.Empty;
        MoreCommands = [];
    }
}
