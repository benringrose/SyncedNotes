using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Commands;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Pages;

/// <summary>
/// The quick-capture page: type a note in the search box and press Enter to save it.
/// The server derives the title from the first line. Ctrl+Enter saves and opens the note;
/// the context menu (Ctrl+K) offers "Save as pinned note" and the full form.
/// </summary>
internal sealed partial class NewNotePage : DynamicListPage
{
    private readonly SyncedNotesService _service;

    public NewNotePage(SyncedNotesService service)
    {
        _service = service;

        Id = "SyncedNotes.CmdPal.NewNote";
        Name = "Open";
        Title = "New note";
        Icon = Icons.NewNote;
        PlaceholderText = "Type a note and press Enter to save it to SyncedNotes";
        EmptyContent = new CommandItem(new NoOpCommand())
        {
            Title = "Type a note above and press Enter",
            Subtitle = "The first line becomes the title. Ctrl+Enter saves and opens it.",
            Icon = Icons.NewNote,
        };

        _service.LastSavedChanged += (_, _) => RaiseItemsChanged();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!string.Equals(oldSearch, newSearch, StringComparison.Ordinal))
        {
            RaiseItemsChanged();
        }
    }

    public override IListItem[] GetItems()
    {
        if (!_service.Settings.TryGetConnection(out _, out var problem))
        {
            return [_service.CreateSetupListItem(problem)];
        }

        // Warm the binder list so the full form opens with its dropdown ready.
        _service.Binders.Prefetch();

        var text = SearchText ?? string.Empty;
        var items = new List<IListItem>(3);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var draft = NoteDraft.FromText(text);
            items.Add(CreateSaveItem(draft));
            items.Add(new ListItem(new NewNoteFormPage(_service, draft.Content))
            {
                Title = "Continue in the full note form…",
                Subtitle = "Add a title, tags, a binder or more text before saving",
                Icon = Icons.Form,
            });
        }
        else
        {
            if (_service.LastSaved is { } lastSaved && SyncedNotesService.CreateOpenSavedNoteItem(lastSaved) is { } openItem)
            {
                items.Add(openItem);
            }

            items.Add(new ListItem(new NewNoteFormPage(_service, initialContent: null))
            {
                Title = "New note with title, tags and binder…",
                Subtitle = "Opens the full note form",
                Icon = Icons.Form,
            });
        }

        return [.. items];
    }

    private ListItem CreateSaveItem(NoteDraft draft)
    {
        return new ListItem(new SaveNoteCommand(_service, draft, behavior: null, progressPage: this, onSaved: OnSaved))
        {
            Title = $"Save note: {TextHelpers.Ellipsize(draft.Content, 90)}",
            Subtitle = "Enter to save · Ctrl+Enter to save and open · the first line becomes the title",
            Icon = Icons.Save,
            MoreCommands =
            [
                // The first context command is the secondary command (Ctrl+Enter).
                new CommandContextItem(new SaveNoteCommand(_service, draft, AfterSaveBehavior.OpenNote, this, OnSaved))
                {
                    Title = "Save and open in browser",
                },
                new CommandContextItem(new SaveNoteCommand(_service, draft with { Pinned = true }, behavior: null, progressPage: this, onSaved: OnSaved) { Name = "Save as pinned note" })
                {
                    Title = "Save as pinned note",
                    Icon = Icons.PinnedNote,
                },
                new CommandContextItem(new NewNoteFormPage(_service, draft.Content))
                {
                    Title = "Continue in the full note form…",
                    Icon = Icons.Form,
                },
                _service.CreateSettingsContextItem(),
            ],
        };
    }

    private void OnSaved(SavedNote note)
    {
        // Clear the text so that coming back to this page (Command Palette restores a page's
        // SearchText) doesn't offer to save the same note again. Raising PropChanged for
        // SearchText makes the palette update its search box.
        SearchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        RaiseItemsChanged();
    }
}
