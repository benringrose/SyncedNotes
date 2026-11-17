using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Pages;

/// <summary>A search result. Enter opens the note; the context menu copies its link or title.</summary>
internal sealed partial class NoteListItem : ListItem
{
    private const int MaxVisibleTags = 3;
    private const long MaxUnixTimeMilliseconds = 253_402_300_799_999; // 9999-12-31T23:59:59.999Z

    public NoteListItem(SyncedNotesService service, NoteSummary note)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : TextHelpers.SingleLine(note.Title);
        var url = service.ResolveNoteUrl(note.Url, note.Id);
        var snippet = TextHelpers.SingleLine(note.Snippet);
        var tags = (note.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        var pinned = note.Pinned == true;
        DateTimeOffset? updated = note.UpdatedAt is long ms && ms > 0 && ms <= MaxUnixTimeMilliseconds
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

        Command = url.Length > 0
            ? new OpenUrlCommand(url) { Name = "Open note", Icon = Icons.Open, Result = CommandResult.Dismiss() }
            : new NoOpCommand { Name = "No link available" };
        Title = title;
        Subtitle = snippet.Length > 0
            ? TextHelpers.Ellipsize(snippet, 160)
            : updated is { } u ? $"Updated {TextHelpers.FormatRelative(u, DateTimeOffset.UtcNow)}" : string.Empty;
        Icon = pinned ? Icons.PinnedNote : Icons.Note;
        Tags = BuildTags(note.Binder, tags, pinned);

        var moreCommands = new List<IContextItem>();
        if (url.Length > 0)
        {
            moreCommands.Add(new CommandContextItem(new CopyTextCommand(url) { Name = "Copy link" })
            {
                Title = "Copy link",
                Icon = Icons.Link,
                RequestedShortcut = SyncedNotesService.CopyLinkShortcut,
            });
        }

        moreCommands.Add(new CommandContextItem(new CopyTextCommand(title) { Name = "Copy title" })
        {
            Title = "Copy title",
            Icon = Icons.Copy,
        });

        if (url.Length > 0)
        {
            moreCommands.Add(new CommandContextItem(new CopyTextCommand($"[{EscapeMarkdownLinkText(title)}]({url})") { Name = "Copy Markdown link" })
            {
                Title = "Copy Markdown link",
                Icon = Icons.Link,
            });
        }

        moreCommands.Add(service.CreateSettingsContextItem());
        MoreCommands = [.. moreCommands];

        Details = BuildDetails(title, snippet, note.Binder, tags, pinned, updated, url);
    }

    private static ITag[] BuildTags(string? binder, List<string> tags, bool pinned)
    {
        var result = new List<ITag>();
        if (!string.IsNullOrWhiteSpace(binder))
        {
            result.Add(new Tag(binder.Trim()) { Icon = Icons.Binder, ToolTip = "Binder" });
        }

        result.AddRange(tags.Take(MaxVisibleTags).Select(t => new Tag("#" + t)));
        if (tags.Count > MaxVisibleTags)
        {
            result.Add(new Tag($"+{tags.Count - MaxVisibleTags}") { ToolTip = string.Join(", ", tags.Skip(MaxVisibleTags)) });
        }

        if (pinned && result.Count == 0)
        {
            result.Add(new Tag("Pinned") { Icon = Icons.PinnedNote });
        }

        return [.. result];
    }

    private static Details BuildDetails(string title, string snippet, string? binder, List<string> tags, bool pinned, DateTimeOffset? updated, string url)
    {
        var metadata = new List<IDetailsElement>();
        if (!string.IsNullOrWhiteSpace(binder))
        {
            metadata.Add(new DetailsElement { Key = "Binder", Data = new DetailsLink { Text = binder.Trim() } });
        }

        if (tags.Count > 0)
        {
            metadata.Add(new DetailsElement { Key = "Tags", Data = new DetailsTags { Tags = [.. tags.Select(t => new Tag("#" + t))] } });
        }

        if (pinned)
        {
            metadata.Add(new DetailsElement { Key = "Pinned", Data = new DetailsLink { Text = "Yes" } });
        }

        if (updated is { } u)
        {
            metadata.Add(new DetailsElement { Key = "Updated", Data = new DetailsLink { Text = TextHelpers.FormatTimestamp(u) } });
        }

        if (url.Length > 0)
        {
            metadata.Add(new DetailsElement { Key = "Link", Data = new DetailsLink(url, "Open in browser") });
        }

        return new Details
        {
            Title = title,
            Body = snippet.Length > 0 ? snippet : "_No preview available._",
            Metadata = [.. metadata],
        };
    }

    private static string EscapeMarkdownLinkText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
