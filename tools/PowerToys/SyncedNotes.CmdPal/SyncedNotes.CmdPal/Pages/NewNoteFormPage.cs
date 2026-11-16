using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SyncedNotes.CmdPal.Helpers;
using SyncedNotes.CmdPal.Services;

namespace SyncedNotes.CmdPal.Pages;

/// <summary>The full "new note" form: title, content, tags, binder and pinned.</summary>
internal sealed partial class NewNoteFormPage : ContentPage
{
    // How long opening the form may wait for the binder list (usually already prefetched).
    private static readonly TimeSpan BinderWaitTimeout = TimeSpan.FromSeconds(1.5);

    private readonly SyncedNotesService _service;
    private readonly string? _initialContent;

    public NewNoteFormPage(SyncedNotesService service, string? initialContent)
    {
        _service = service;
        _initialContent = initialContent;

        Id = "SyncedNotes.CmdPal.NewNoteForm";
        Name = "Open form";
        Title = "New note";
        Icon = Icons.Form;
        Commands = [_service.CreateSettingsContextItem()];
    }

    public override IContent[] GetContent()
    {
        if (!_service.Settings.TryGetConnection(out _, out var problem))
        {
            return
            [
                new MarkdownContent(
                    $"## Set up SyncedNotes first\n\n{problem}\n\n" +
                    "Open **SyncedNotes settings** from this page's context menu (Ctrl+K), or go to " +
                    "Command Palette settings → Extensions → SyncedNotes."),
            ];
        }

        // A new form object per call, so the binder list is current every time the page opens.
        return [new NoteForm(_service, _initialContent, WaitForBinders())];
    }

    private IReadOnlyList<BinderInfo>? WaitForBinders()
    {
        var task = _service.Binders.GetAsync();
        try
        {
            return task.Wait(BinderWaitTimeout) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null; // falls back to a free-text binder field
        }
    }
}

/// <summary>
/// Adaptive Card form for a new note. User-provided values (prefilled content, binder names)
/// are passed through <see cref="FormContent.DataJson"/> and bound with <c>${...}</c>, so they
/// are never parsed as part of the card template.
/// </summary>
internal sealed partial class NoteForm : FormContent
{
    private const string NoBinderValue = "__none__";

    private const string TemplateStart = """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.6",
          "body": [
            {
              "type": "Input.Text",
              "id": "title",
              "label": "Title",
              "placeholder": "Optional: defaults to the first line of the note",
              "maxLength": 200,
              "value": "${title}"
            },
            {
              "type": "Input.Text",
              "id": "content",
              "label": "Note",
              "placeholder": "Write your note (Markdown is supported)",
              "isMultiline": true,
              "value": "${content}"
            },
            {
              "type": "Input.Text",
              "id": "tags",
              "label": "Tags",
              "placeholder": "Comma-separated, for example: work, ideas",
              "value": "${tags}"
            },
        """;

    // Used when the binder list is known: a dropdown of the user's binders.
    private const string BinderChoices = """
            {
              "type": "Input.ChoiceSet",
              "id": "binder",
              "label": "Binder",
              "style": "compact",
              "value": "__none__",
              "choices": [
                { "title": "No binder", "value": "__none__" },
                { "$data": "${binders}", "title": "${name}", "value": "${id}" }
              ]
            },
        """;

    // Used when the binders couldn't be loaded: the API accepts a binder name and ignores unknown ones.
    private const string BinderTextBox = """
            {
              "type": "Input.Text",
              "id": "binderName",
              "label": "Binder",
              "placeholder": "Optional binder name (ignored if it doesn't exist)",
              "value": ""
            },
        """;

    private const string TemplateEnd = """
            {
              "type": "Input.Toggle",
              "id": "pinned",
              "title": "Pin this note",
              "valueOn": "true",
              "valueOff": "false",
              "value": "false"
            }
          ],
          "actions": [
            { "type": "Action.Submit", "title": "Save note", "data": { "action": "save" } },
            { "type": "Action.Submit", "title": "Save and open", "data": { "action": "open" } }
          ]
        }
        """;

    private readonly SyncedNotesService _service;
    private readonly IReadOnlyList<BinderInfo>? _binders;

    public NoteForm(SyncedNotesService service, string? initialContent, IReadOnlyList<BinderInfo>? binders)
    {
        _service = service;
        _binders = binders;

        TemplateJson = TemplateStart + (binders is null ? BinderTextBox : BinderChoices) + TemplateEnd;
        DataJson = BuildDataJson(initialContent, binders);
    }

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        if (TryParseObject(inputs) is not { } form)
        {
            return CommandResult.KeepOpen();
        }

        var draft = new NoteDraft(
            Title: GetString(form, "title"),
            Content: GetString(form, "content") ?? string.Empty,
            Tags: TextHelpers.ParseTags(GetString(form, "tags")),
            Binder: ResolveBinderName(form),
            Pinned: string.Equals(GetString(form, "pinned"), "true", StringComparison.OrdinalIgnoreCase));

        var openAfterSave = TryParseObject(data) is { } action && GetString(action, "action") == "open";
        var behavior = openAfterSave ? AfterSaveBehavior.OpenNote : _service.Settings.AfterSave;

        // "Stay open" goes back to the page the form was opened from (it offers to open the note).
        return _service.SaveNote(draft, behavior, CommandResult.GoBack());
    }

    private static string BuildDataJson(string? initialContent, IReadOnlyList<BinderInfo>? binders)
    {
        var binderNodes = (binders ?? [])
            .Where(b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.Name))
            .Select(b => (JsonNode?)new JsonObject { ["id"] = b.Id, ["name"] = b.Name })
            .ToArray();

        var data = new JsonObject
        {
            ["title"] = string.Empty,
            ["content"] = initialContent ?? string.Empty,
            ["tags"] = string.Empty,
            ["binders"] = new JsonArray(binderNodes),
        };

        return data.ToJsonString();
    }

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonObject obj, string key) => obj[key] switch
    {
        null => null,
        JsonValue value when value.TryGetValue(out string? text) => text,
        var other => other.ToString(),
    };

    private string? ResolveBinderName(JsonObject form)
    {
        // Free-text variant
        var typedName = GetString(form, "binderName");
        if (!string.IsNullOrWhiteSpace(typedName))
        {
            return typedName.Trim();
        }

        // Dropdown variant: the choice value is the binder id; the API wants the name.
        var id = GetString(form, "binder");
        if (string.IsNullOrEmpty(id) || id == NoBinderValue || _binders is null)
        {
            return null;
        }

        return _binders.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))?.Name;
    }
}
