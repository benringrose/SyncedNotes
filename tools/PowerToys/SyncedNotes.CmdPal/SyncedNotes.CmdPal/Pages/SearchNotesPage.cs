using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Pages;

/// <summary>
/// Searches notes on the server as you type (debounced, stale requests are cancelled).
/// An empty query lists the most recent notes. Enter opens a note in the browser.
/// </summary>
internal sealed partial class SearchNotesPage : DynamicListPage, IDisposable
{
    private const int ResultLimit = 30;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    // When the page is shown again after this long, cached results are shown and refreshed in the background.
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(30);

    private readonly SyncedNotesService _service;
    private readonly Lock _lock = new();
    private readonly CommandItem _noNotesYet;
    private readonly CommandItem _noMatches;

    private CancellationTokenSource? _searchCts;
    private IListItem[] _items = [];
    private bool _started;
    private DateTimeOffset _lastCompleted = DateTimeOffset.MinValue;

    public SearchNotesPage(SyncedNotesService service)
    {
        _service = service;

        Id = "SyncedNotes.CmdPal.SearchNotes";
        Name = "Search";
        Title = "Search notes";
        Icon = Icons.Search;
        PlaceholderText = "Search your SyncedNotes notes…";
        ShowDetails = false; // details are still available from each result's context menu

        _noNotesYet = new CommandItem(new NoOpCommand())
        {
            Title = "No notes yet",
            Subtitle = "Create one with “New note”.",
            Icon = Icons.Search,
        };
        _noMatches = new CommandItem(new NoOpCommand())
        {
            Title = "No matching notes",
            Subtitle = "Archived and trashed notes aren't included in the search.",
            Icon = Icons.Search,
        };
        EmptyContent = _noNotesYet;

        // A note saved from the palette should show up when the list is opened next.
        _service.LastSavedChanged += (_, _) => MarkStale();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!string.Equals(oldSearch, newSearch, StringComparison.Ordinal))
        {
            StartSearch(newSearch, debounce: true);
        }
    }

    public override IListItem[] GetItems()
    {
        if (!_service.Settings.TryGetConnection(out _, out var problem))
        {
            return [_service.CreateSetupListItem(problem)];
        }

        bool start;
        lock (_lock)
        {
            // First visit (or after a reset), or cached results that are getting old and no
            // search is running: (re)load results for the current text in the background.
            start = !_started || (_searchCts is null && DateTimeOffset.UtcNow - _lastCompleted > RefreshAfter);
        }

        if (start)
        {
            StartSearch(SearchText ?? string.Empty, debounce: false);
        }

        lock (_lock)
        {
            return _items;
        }
    }

    /// <summary>Drops cached results, e.g. after the settings changed. The next visit searches again.</summary>
    public void Reset()
    {
        CancellationTokenSource? previous;
        lock (_lock)
        {
            previous = _searchCts;
            _searchCts = null;
            _items = [];
            _started = false;
        }

        previous?.Cancel();
        IsLoading = false;
        RaiseItemsChanged();
    }

    /// <summary>Keeps the cached results but refreshes them the next time the page asks for items.</summary>
    public void MarkStale()
    {
        lock (_lock)
        {
            _lastCompleted = DateTimeOffset.MinValue;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_lock)
        {
            previous = _searchCts;
            _searchCts = null;
        }

        previous?.Cancel();
    }

    private void StartSearch(string query, bool debounce)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_lock)
        {
            previous = _searchCts;
            _searchCts = cts;
            _started = true;
        }

        // Cancel the stale request (if it is still waiting or running); its results are dropped.
        previous?.Cancel();
        IsLoading = true;
        _ = RunSearchAsync(query, debounce, cts);
    }

    private async Task RunSearchAsync(string query, bool debounce, CancellationTokenSource cts)
    {
        var token = cts.Token;
        IListItem[] items;
        try
        {
            if (debounce)
            {
                await Task.Delay(DebounceDelay, token).ConfigureAwait(false);
            }

            var notes = await _service.Client.SearchNotesAsync(query, ResultLimit, token).ConfigureAwait(false);
            items = [.. notes.Where(n => !string.IsNullOrWhiteSpace(n.Id)).Select(n => new NoteListItem(_service, n))];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return; // superseded by a newer search
        }
        catch (SyncedNotesException ex)
        {
            items = [CreateErrorItem(ex, query)];
        }
        catch (Exception ex)
        {
            // Never leave the page spinning because of an unexpected bug.
            ExtensionHost.LogMessage(new LogMessage { Message = $"SyncedNotes: search failed unexpectedly ({ex.GetType().Name})", State = MessageState.Error });
            items = [CreateErrorItem(new SyncedNotesException($"Something went wrong ({ex.GetType().Name})."), query)];
        }

        lock (_lock)
        {
            if (!ReferenceEquals(_searchCts, cts))
            {
                return; // superseded while finishing
            }

            _searchCts = null;
            _items = items;
            _lastCompleted = DateTimeOffset.UtcNow;
        }

        EmptyContent = string.IsNullOrWhiteSpace(query) ? _noNotesYet : _noMatches;
        IsLoading = false;
        RaiseItemsChanged(items.Length);
    }

    private ListItem CreateErrorItem(SyncedNotesException error, string query)
    {
        if (error.IsSettingsProblem)
        {
            return new ListItem(_service.SettingsPage)
            {
                Title = "Couldn't search SyncedNotes: check the extension settings",
                Subtitle = error.Message,
                Icon = Icons.Warning,
                MoreCommands = [new CommandContextItem(new AnonymousCommand(() => StartSearch(query, debounce: false)) { Name = "Retry", Icon = Icons.Refresh, Result = CommandResult.KeepOpen() })],
            };
        }

        return new ListItem(new AnonymousCommand(() => StartSearch(query, debounce: false)) { Name = "Retry", Icon = Icons.Refresh, Result = CommandResult.KeepOpen() })
        {
            Title = "Couldn't search SyncedNotes (press Enter to retry)",
            Subtitle = error.Message,
            Icon = Icons.Warning,
            MoreCommands = [_service.CreateSettingsContextItem()],
        };
    }
}
