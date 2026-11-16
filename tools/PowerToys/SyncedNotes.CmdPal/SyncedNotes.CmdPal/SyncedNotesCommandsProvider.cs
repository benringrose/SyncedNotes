using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Fallbacks;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Pages;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal;

public partial class SyncedNotesCommandsProvider : CommandProvider
{
    private const string OpenCommandId = "SyncedNotes.CmdPal.OpenSyncedNotes";

    private readonly SettingsManager _settingsManager = new();
    private readonly SyncedNotesService _service;
    private readonly SearchNotesPage _searchPage;
    private readonly CommandItem _setupItem;
    private readonly CommandItem _newNoteItem;
    private readonly CommandItem _searchItem;
    private readonly CommandItem _openItem;
    private readonly IFallbackCommandItem[] _fallbacks;
    private volatile ICommandItem[] _commands = [];

    public SyncedNotesCommandsProvider()
    {
        DisplayName = "SyncedNotes";
        Icon = Icons.SyncedNotes;
        Settings = _settingsManager.Settings;

        _service = new SyncedNotesService(_settingsManager);
        _searchPage = new SearchNotesPage(_service);

        _setupItem = new CommandItem(_service.SettingsPage)
        {
            Title = "Set up SyncedNotes",
            Subtitle = "Enter your SyncedNotes server URL and API token",
            Icon = Icons.Settings,
        };

        _newNoteItem = new CommandItem(new NewNotePage(_service))
        {
            Title = "New note",
            Subtitle = "SyncedNotes: type a note and press Enter to save it",
            Icon = Icons.NewNote,
            MoreCommands =
            [
                new CommandContextItem(new NewNoteFormPage(_service, initialContent: null))
                {
                    Title = "New note with title, tags and binder…",
                    Icon = Icons.Form,
                },
                _service.CreateSettingsContextItem(),
            ],
        };

        _searchItem = new CommandItem(_searchPage)
        {
            Title = "Search notes",
            Subtitle = "SyncedNotes: find a note and open it",
            Icon = Icons.Search,
            MoreCommands = [_service.CreateSettingsContextItem()],
        };

        _openItem = new CommandItem(new NoOpCommand { Id = OpenCommandId })
        {
            Title = "Open SyncedNotes",
            Icon = Icons.Globe,
            MoreCommands = [_service.CreateSettingsContextItem()],
        };

        _fallbacks = [new AddNoteFallbackItem(_service)];

        ApplySettings();
        _settingsManager.Settings.SettingsChanged += OnSettingsChanged;
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override IFallbackCommandItem[] FallbackCommands() => _fallbacks;

    public override void Dispose()
    {
        _settingsManager.Settings.SettingsChanged -= OnSettingsChanged;
        _searchPage.Dispose();
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        _service.OnSettingsChanged();
        _searchPage.Reset();
        ApplySettings();

        // Ask Command Palette to fetch TopLevelCommands() again (the setup item comes and goes).
        RaiseItemsChanged();
    }

    private void ApplySettings()
    {
        if (_settingsManager.TryGetConnection(out var connection, out _))
        {
            _openItem.Command = new OpenUrlCommand(connection.BaseUrl + "/")
            {
                Id = OpenCommandId,
                Name = "Open",
                Icon = Icons.Globe,
                Result = CommandResult.Dismiss(),
            };
            _openItem.Subtitle = connection.BaseUrl;
            _commands = [_newNoteItem, _searchItem, _openItem];
        }
        else
        {
            _commands = [_setupItem, _newNoteItem, _searchItem];
        }
    }
}
