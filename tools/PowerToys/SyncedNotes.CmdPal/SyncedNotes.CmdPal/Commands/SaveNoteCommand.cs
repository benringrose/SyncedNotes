using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Commands;

/// <summary>Creates a note from a <see cref="NoteDraft"/> when invoked.</summary>
internal sealed partial class SaveNoteCommand : InvokableCommand
{
    private readonly SyncedNotesService _service;
    private readonly NoteDraft _draft;
    private readonly AfterSaveBehavior? _behavior;
    private readonly Page? _progressPage;
    private readonly Action<SavedNote>? _onSaved;

    /// <param name="service">Shared extension state.</param>
    /// <param name="draft">The note to create.</param>
    /// <param name="behavior">What happens after saving; <see langword="null"/> uses the "After saving a note" setting.</param>
    /// <param name="progressPage">A page whose loading indicator is shown while saving.</param>
    /// <param name="onSaved">Called after a successful save.</param>
    public SaveNoteCommand(
        SyncedNotesService service,
        NoteDraft draft,
        AfterSaveBehavior? behavior = null,
        Page? progressPage = null,
        Action<SavedNote>? onSaved = null)
    {
        _service = service;
        _draft = draft;
        _behavior = behavior;
        _progressPage = progressPage;
        _onSaved = onSaved;

        Name = behavior == AfterSaveBehavior.OpenNote ? "Save and open" : "Save note";
        Icon = behavior == AfterSaveBehavior.OpenNote ? Icons.Open : Icons.Save;
    }

    public override ICommandResult Invoke()
    {
        if (_progressPage is not null)
        {
            _progressPage.IsLoading = true;
        }

        try
        {
            return _service.SaveNote(_draft, _behavior ?? _service.Settings.AfterSave, CommandResult.KeepOpen(), _onSaved);
        }
        finally
        {
            if (_progressPage is not null)
            {
                _progressPage.IsLoading = false;
            }
        }
    }
}
