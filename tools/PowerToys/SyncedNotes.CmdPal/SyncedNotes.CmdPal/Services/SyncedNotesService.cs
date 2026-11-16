using System;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;
using Windows.System;

namespace SyncedNotes.CmdPal.Services;

/// <summary>
/// State and helpers shared by all pages and commands: settings, the API client, the binder
/// cache, the most recently saved note, and the "save a note, then decide what the palette
/// does next" logic.
/// </summary>
internal sealed class SyncedNotesService
{
    private int _saving;
    private SavedNote? _lastSaved;

    public SyncedNotesService(SettingsManager settings)
    {
        Settings = settings;
        Client = new SyncedNotesClient(settings);
        Binders = new BinderCache(Client);

        // The toolkit builds this page (a form for all settings) from the Settings object;
        // keep a single instance so it can be reused by every "open settings" item.
        SettingsPage = settings.Settings.SettingsPage;
    }

    /// <summary>Raised (on an arbitrary thread) after a note was saved.</summary>
    public event EventHandler? LastSavedChanged;

    public static KeyChord CopyLinkShortcut { get; } = KeyChordHelpers.FromModifiers(ctrl: true, shift: true, vkey: VirtualKey.C);

    public SettingsManager Settings { get; }

    public SyncedNotesClient Client { get; }

    public BinderCache Binders { get; }

    public IContentPage SettingsPage { get; }

    public SavedNote? LastSaved => Volatile.Read(ref _lastSaved);

    /// <summary>Shows an error in Command Palette's status area for a few seconds.</summary>
    public static void ShowError(string message) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = MessageState.Error }) { Duration = 8000 }.Show();

    public static bool OpenInBrowser(string url) => url.Length > 0 && ShellHelpers.OpenInShell(url);

    public CommandContextItem CreateSettingsContextItem() =>
        new(SettingsPage) { Title = "SyncedNotes settings", Icon = Icons.Settings };

    /// <summary>The "not configured yet" row that opens the extension settings.</summary>
    public ListItem CreateSetupListItem(string problem) =>
        new(SettingsPage) { Title = "Set up SyncedNotes", Subtitle = problem, Icon = Icons.Settings };

    /// <summary>A row that opens a note that was just saved.</summary>
    public static ListItem? CreateOpenSavedNoteItem(SavedNote note)
    {
        if (note.Url.Length == 0)
        {
            return null;
        }

        return new ListItem(new OpenUrlCommand(note.Url) { Name = "Open note", Icon = Icons.Open, Result = CommandResult.Dismiss() })
        {
            Title = $"Open “{TextHelpers.Ellipsize(note.Title, 70)}”",
            Subtitle = "The note you just saved",
            Icon = Icons.Open,
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(note.Url) { Name = "Copy link" })
                {
                    Title = "Copy link",
                    Icon = Icons.Link,
                    RequestedShortcut = CopyLinkShortcut,
                },
            ],
        };
    }

    /// <summary>
    /// Builds the link to open for a note. Only absolute http(s) URLs from the server are used;
    /// anything else (a file path, another URI scheme) is never handed to the shell.
    /// </summary>
    public string ResolveNoteUrl(string? url, string? id)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.AbsoluteUri;
        }

        if (Guid.TryParse(id, out var guid) && Settings.TryGetConnection(out var connection, out _))
        {
            return $"{connection.BaseUrl}/#/note/{guid:D}";
        }

        return string.Empty;
    }

    public void OnSettingsChanged() => Binders.Invalidate();

    /// <summary>
    /// Creates the note (blocking the calling thread, which is fine inside
    /// <c>Invoke</c>/<c>SubmitForm</c>: they run in this extension's process, not on the
    /// palette's UI thread) and returns what Command Palette should do next.
    /// Errors are shown in the palette and keep it open so the text isn't lost.
    /// </summary>
    /// <param name="draft">The note to create.</param>
    /// <param name="behavior">What to do after a successful save.</param>
    /// <param name="stayOpenResult">The navigation for <see cref="AfterSaveBehavior.StayOpen"/> (KeepOpen on list pages, GoBack on the form).</param>
    /// <param name="onSaved">Called after a successful save, before the result is returned.</param>
    public ICommandResult SaveNote(NoteDraft draft, AfterSaveBehavior behavior, ICommandResult stayOpenResult, Action<SavedNote>? onSaved = null)
    {
        if (!Settings.TryGetConnection(out _, out var problem))
        {
            ShowError(problem);
            return CommandResult.KeepOpen();
        }

        if (draft.IsEmpty)
        {
            ShowError("Type a title or some text first.");
            return CommandResult.KeepOpen();
        }

        if (Interlocked.Exchange(ref _saving, 1) == 1)
        {
            return CommandResult.KeepOpen(); // a save is already running (e.g. Enter pressed twice)
        }

        SavedNote saved;
        try
        {
            var created = Client.CreateNoteAsync(draft.ToRequest(), CancellationToken.None).GetAwaiter().GetResult();
            var title = string.IsNullOrWhiteSpace(created.Title) ? draft.DisplayTitle : created.Title.Trim();
            saved = new SavedNote(created.Id ?? string.Empty, title, ResolveNoteUrl(created.Url, created.Id));
        }
        catch (SyncedNotesException ex)
        {
            // These messages already say "couldn't save the note"; the others need the context.
            var alreadyExplained = ex.Kind is SyncedNotesErrorKind.BadRequest or SyncedNotesErrorKind.Forbidden or SyncedNotesErrorKind.HttpError;
            ShowError(alreadyExplained ? ex.Message : $"Couldn't save the note. {ex.Message}");
            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            // Unexpected bug: keep the palette (and the typed text) instead of failing the COM call.
            ExtensionHost.LogMessage(new LogMessage { Message = $"SyncedNotes: save failed unexpectedly ({ex.GetType().Name})", State = MessageState.Error });
            ShowError($"Couldn't save the note: something went wrong ({ex.GetType().Name}).");
            return CommandResult.KeepOpen();
        }
        finally
        {
            Volatile.Write(ref _saving, 0);
        }

        Volatile.Write(ref _lastSaved, saved);
        LastSavedChanged?.Invoke(this, EventArgs.Empty);
        onSaved?.Invoke(saved);

        var message = $"Saved “{TextHelpers.Ellipsize(saved.Title, 60)}” to SyncedNotes";
        return behavior switch
        {
            // Opening the browser takes focus anyway; dismiss and confirm with a toast.
            AfterSaveBehavior.OpenNote when OpenInBrowser(saved.Url) => CommandResult.ShowToast(message),
            AfterSaveBehavior.StayOpen => CommandResult.ShowToast(new ToastArgs { Message = message, Result = stayOpenResult }),

            // ShowToast(string) dismisses the palette after showing the toast.
            _ => CommandResult.ShowToast(message),
        };
    }
}
