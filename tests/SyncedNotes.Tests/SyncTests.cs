using System.Net;
using System.Text.Json;
using SyncedNotes.Data;
using SyncedNotes.Sync;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>The offline-first sync protocol: GET /api/sync?since=N and POST /api/sync.</summary>
public class SyncTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private async Task<HttpClient> NewUserClientAsync() => factory.CreateClientFor(await factory.CreateUserAsync());

    /// <summary>Two devices of the same account.</summary>
    private async Task<(HttpClient A, HttpClient B)> TwoDevicesAsync()
    {
        var user = await factory.CreateUserAsync();
        return (factory.CreateClientFor(user), factory.CreateClientFor(user));
    }

    // ------------------------------------------------------------------------------- Push and pull

    [Fact]
    public async Task Pushing_a_new_binder_and_note_returns_revisions_and_a_pull_returns_them()
    {
        var client = await NewUserClientAsync();
        var binder = NewBinder("Work", color: "blue", icon: "bi-briefcase");
        var note = NewNote("Plan", "# Plan\n- ship it", binderId: binder.Id, tags: ["roadmap"]);
        note.Pinned = true;
        note.SortOrder = 2.5;

        var before = NowMs();
        var push = await client.PushAsync(cursor: 0, notes: [note], binders: [binder]);

        var binderResult = push.Results!.Binders.For(binder.Id);
        var noteResult = push.Results.Notes.For(note.Id);
        Assert.Equal(ChangeStatus.Ok, binderResult.Status);
        Assert.Equal(ChangeStatus.Ok, noteResult.Status);
        Assert.Null(noteResult.CopyId);
        Assert.Null(noteResult.Error);
        Assert.True(binderResult.Rev > 0);
        Assert.True(noteResult.Rev > binderResult.Rev, "binders are applied before the notes that reference them");
        Assert.Equal(noteResult.Rev, push.Cursor);
        // The push response already carries everything after the client's cursor, including its own changes.
        Assert.Contains(push.Notes, n => n.Id == note.Id);
        Assert.Contains(push.Binders, b => b.Id == binder.Id);

        var pull = await client.PullAsync(since: 0);
        Assert.Equal(push.Cursor, pull.Cursor);
        Assert.False(pull.HasMore);
        Assert.False(pull.Reset);
        Assert.InRange(pull.ServerTime, before, NowMs());

        var pulledBinder = Assert.Single(pull.Binders);
        Assert.Equal(binder.Id, pulledBinder.Id);
        Assert.Equal("Work", pulledBinder.Name);
        Assert.Equal("blue", pulledBinder.Color);
        Assert.Equal("bi-briefcase", pulledBinder.Icon);
        Assert.Equal(binderResult.Rev, pulledBinder.Rev);
        Assert.False(pulledBinder.Deleted);

        var pulledNote = Assert.Single(pull.Notes);
        Assert.Equal(note.Id, pulledNote.Id);
        Assert.Equal(binder.Id, pulledNote.BinderId);
        Assert.Equal("Plan", pulledNote.Title);
        Assert.Equal("# Plan\n- ship it", pulledNote.Content);
        Assert.Equal(new[] { "roadmap" }, pulledNote.Tags);
        Assert.True(pulledNote.Pinned);
        Assert.False(pulledNote.Archived);
        Assert.Null(pulledNote.TrashedAt);
        Assert.Equal(2.5, pulledNote.SortOrder);
        Assert.Equal(note.CreatedAt, pulledNote.CreatedAt);
        Assert.Equal(note.UpdatedAt, pulledNote.UpdatedAt);
        Assert.Equal(noteResult.Rev, pulledNote.Rev);
        Assert.False(pulledNote.Deleted);

        // Pulling again from the returned cursor yields nothing new.
        var next = await client.PullAsync(since: pull.Cursor);
        Assert.Empty(next.Notes);
        Assert.Empty(next.Binders);
        Assert.Null(next.Preferences);
        Assert.Equal(pull.Cursor, next.Cursor);
        Assert.False(next.HasMore);
        Assert.False(next.Reset);
    }

    [Fact]
    public async Task Revisions_increase_with_every_change_and_are_counted_per_user()
    {
        var alice = await NewUserClientAsync();
        var bob = await NewUserClientAsync();

        var a1 = NewNote("a1");
        var a2 = NewNote("a2");
        var a3 = NewNote("a3");
        var first = await alice.PushNotesAsync(a1, a2, a3);
        Assert.Equal(new long[] { 1, 2, 3 }, first.Results!.Notes.Select(r => r.Rev));

        var edit = EditOf(first.Notes.Single(n => n.Id == a2.Id), SyncFields.Content);
        edit.Content = "changed";
        var second = await alice.PushAsync(cursor: first.Cursor, notes: [edit]);
        Assert.Equal(4, second.Results!.Notes.For(a2.Id).Rev);
        Assert.Equal(4, second.Cursor);

        // Bob has his own change sequence, unaffected by Alice's changes.
        var b1 = NewNote("b1");
        var bobPush = await bob.PushNotesAsync(b1);
        Assert.Equal(1, bobPush.Results!.Notes.For(b1.Id).Rev);
        Assert.Equal(1, bobPush.Cursor);

        // A pull returns exactly the changes after the given revision, oldest first.
        var since2 = await alice.PullAsync(since: 2);
        Assert.Equal(new[] { a3.Id, a2.Id }, since2.Notes.Select(n => n.Id));
        Assert.Equal(new long[] { 3, 4 }, since2.Notes.Select(n => n.Rev));
        Assert.Equal(4, since2.Cursor);
    }

    [Fact]
    public async Task Tombstones_are_only_returned_to_devices_that_have_synced_before()
    {
        var client = await NewUserClientAsync();
        var keep = NewNote("keep");
        var doomed = NewNote("doomed", "secret text", tags: ["private"]);
        var binder = NewBinder("Old binder");
        var created = await client.PushAsync(notes: [keep, doomed], binders: [binder]);
        var seen = created.Notes.Single(n => n.Id == doomed.Id);

        var binderDelete = NewBinder("Old binder", id: binder.Id);
        binderDelete.Deleted = true;
        var deleted = await client.PushAsync(cursor: created.Cursor, notes: [DeleteOf(seen)], binders: [binderDelete]);
        var deleteResult = deleted.Results!.Notes.For(doomed.Id);
        Assert.Equal(ChangeStatus.Ok, deleteResult.Status);
        Assert.True(deleteResult.Rev > created.Cursor);

        // A brand-new device (since = 0) never hears about deleted records...
        var full = await client.PullAsync(since: 0);
        Assert.Equal(new[] { keep.Id }, full.Notes.Select(n => n.Id));
        Assert.Empty(full.Binders);

        // ...while a device that synced before receives tombstones, without the deleted content.
        var incremental = await client.PullAsync(since: created.Cursor);
        var tombstone = Assert.Single(incremental.Notes);
        Assert.Equal(doomed.Id, tombstone.Id);
        Assert.True(tombstone.Deleted);
        Assert.Equal(deleteResult.Rev, tombstone.Rev);
        Assert.Equal("", tombstone.Title);
        Assert.Equal("", tombstone.Content);
        Assert.Empty(tombstone.Tags);
        var binderTombstone = Assert.Single(incremental.Binders);
        Assert.Equal(binder.Id, binderTombstone.Id);
        Assert.True(binderTombstone.Deleted);

        // Deleting something that is already deleted (e.g. a retry) is fine.
        var again = await client.PushAsync(cursor: deleted.Cursor, notes: [DeleteOf(seen)], binders: [binderDelete]);
        Assert.Equal(ChangeStatus.Ok, again.Results!.Notes.For(doomed.Id).Status);
        Assert.Equal(ChangeStatus.Ok, again.Results.Binders.For(binder.Id).Status);
    }

    [Fact]
    public async Task A_note_created_and_deleted_before_it_ever_synced_is_ignored()
    {
        var client = await NewUserClientAsync();
        var note = NewNote("never synced");
        note.Deleted = true;

        var response = await client.PushNotesAsync(note);

        var result = response.Results!.Notes.For(note.Id);
        Assert.Equal(ChangeStatus.Ok, result.Status);
        Assert.Equal(0, result.Rev);
        Assert.Empty(response.Notes);
        Assert.Equal(0, response.Cursor);
    }

    // ------------------------------------------------------------------------------- Merging and conflicts

    [Fact]
    public async Task Changes_to_different_fields_on_two_devices_are_merged()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var created = await deviceA.PushNotesAsync(NewNote("Recipe", "flour"));
        var original = Assert.Single(created.Notes);

        // Device B edits the text...
        var textEdit = EditOf(original, SyncFields.Content);
        textEdit.Content = "flour, sugar";
        Assert.Equal(ChangeStatus.Ok, (await deviceB.PushNotesAsync(textEdit)).Results!.Notes.For(original.Id).Status);

        // ...while device A, which never saw that edit, pins, tags and colours the note (stale base revision,
        // stale text in the payload, but only the fields it changed are listed).
        var flags = EditOf(original, SyncFields.Pinned, SyncFields.Tags, SyncFields.Color);
        flags.Pinned = true;
        flags.Tags = ["baking"];
        flags.Color = "green";
        var result = (await deviceA.PushNotesAsync(flags)).Results!.Notes.For(original.Id);
        Assert.Equal(ChangeStatus.Ok, result.Status);
        Assert.Null(result.CopyId);

        var merged = await deviceA.FindNoteAsync(original.Id);
        Assert.NotNull(merged);
        Assert.Equal("flour, sugar", merged.Content);   // B's text is kept
        Assert.Equal("Recipe", merged.Title);
        Assert.True(merged.Pinned);                      // A's flags are kept
        Assert.Equal(new[] { "baking" }, merged.Tags);
        Assert.Equal("green", merged.Color);
        Assert.Equal(result.Rev, merged.Rev);
        Assert.Single((await deviceA.PullAsync()).Notes); // and no conflicted copy was made
    }

    [Fact]
    public async Task Concurrent_text_edits_keep_the_first_and_save_the_second_as_a_conflicted_copy()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var binder = NewBinder("Kitchen");
        var created = await deviceA.PushAsync(notes: [NewNote("Shopping", "milk", binderId: binder.Id)], binders: [binder]);
        var original = created.Notes.Single();

        var editA = EditOf(original, SyncFields.Content);
        editA.Content = "milk\neggs";
        var editB = EditOf(original, SyncFields.Content);
        editB.Content = "milk\nbread";

        Assert.Equal(ChangeStatus.Ok, (await deviceA.PushNotesAsync(editA)).Results!.Notes.For(original.Id).Status);
        var responseB = await deviceB.PushAsync(cursor: created.Cursor, notes: [editB]);

        var resultB = responseB.Results!.Notes.For(original.Id);
        Assert.Equal(ChangeStatus.Conflict, resultB.Status);
        Assert.NotNull(resultB.CopyId);
        Assert.NotEqual(original.Id, resultB.CopyId);

        // The server kept the first device's text...
        var serverNote = responseB.Notes.Single(n => n.Id == original.Id);
        Assert.Equal("milk\neggs", serverNote.Content);
        Assert.Equal("Shopping", serverNote.Title);
        Assert.Equal(resultB.Rev, serverNote.Rev);

        // ...and saved the second device's text as a separate note, delivered in the same response.
        var copy = responseB.Notes.Single(n => n.Id == resultB.CopyId);
        Assert.Contains("conflicted copy", copy.Title);
        Assert.StartsWith("Shopping", copy.Title);
        Assert.Equal("milk\nbread", copy.Content);
        Assert.Equal(binder.Id, copy.BinderId);
        Assert.False(copy.Deleted);
        Assert.True(copy.Rev > created.Cursor);

        var everything = (await deviceA.PullAsync()).Notes;
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public async Task Retrying_a_push_that_was_already_applied_does_not_create_a_conflict()
    {
        // A client that lost the response retries the very same change (same base revision).
        var client = await NewUserClientAsync();
        var original = (await client.PushNotesAsync(NewNote("Draft", "v1"))).Notes.Single();
        var edit = EditOf(original, SyncFields.Title, SyncFields.Content);
        edit.Title = "Final";
        edit.Content = "v2";

        var first = (await client.PushNotesAsync(edit)).Results!.Notes.For(original.Id);
        var retry = (await client.PushNotesAsync(edit)).Results!.Notes.For(original.Id);

        Assert.Equal(ChangeStatus.Ok, first.Status);
        Assert.Equal(ChangeStatus.Ok, retry.Status);
        Assert.Null(retry.CopyId);
        var note = Assert.Single((await client.PullAsync()).Notes);
        Assert.Equal(("Final", "v2"), (note.Title, note.Content));
    }

    [Fact]
    public async Task Conflicted_copies_of_notes_with_long_titles_are_still_marked_as_conflicted()
    {
        // Titles may be up to 300 characters; the copy's marker must survive the length limit.
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var title = string.Join(' ', Enumerable.Repeat("Quarterly planning notes", 12)); // 299 characters
        Assert.InRange(title.Length, 280, NoteLimits.MaxTitleLength);
        var original = (await deviceA.PushNotesAsync(NewNote(title, "v1"))).Notes.Single();

        var editA = EditOf(original, SyncFields.Content);
        editA.Content = "from A";
        var editB = EditOf(original, SyncFields.Content);
        editB.Content = "from B";
        await deviceA.PushNotesAsync(editA);
        var resultB = (await deviceB.PushNotesAsync(editB)).Results!.Notes.For(original.Id);

        Assert.Equal(ChangeStatus.Conflict, resultB.Status);
        var copy = await deviceB.FindNoteAsync(resultB.CopyId!.Value);
        Assert.NotNull(copy);
        Assert.Equal("from B", copy.Content);
        Assert.Contains("conflicted copy", copy.Title);
    }

    [Fact]
    public async Task Deleting_with_a_stale_base_conflicts_when_the_text_changed_on_the_server()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var created = await deviceA.PushNotesAsync(NewNote("Draft", "v1"));
        var original = created.Notes.Single();

        var edit = EditOf(original, SyncFields.Content);
        edit.Content = "v2 - important";
        var edited = await deviceA.PushAsync(cursor: created.Cursor, notes: [edit]);
        Assert.Equal(ChangeStatus.Ok, edited.Results!.Notes.For(original.Id).Status);

        // Device B never saw "v2" and deletes the note: the server must not destroy text B has never seen.
        var staleDelete = await deviceB.PushAsync(cursor: created.Cursor, notes: [DeleteOf(original)]);
        var result = staleDelete.Results!.Notes.For(original.Id);
        Assert.Equal(ChangeStatus.Conflict, result.Status);
        Assert.Null(result.CopyId);
        var kept = await deviceB.FindNoteAsync(original.Id);
        Assert.NotNull(kept);
        Assert.False(kept.Deleted);
        Assert.Equal("v2 - important", kept.Content);
        // B gets the text it hadn't seen in the same response.
        Assert.Contains(staleDelete.Notes, n => n.Id == original.Id && n.Content == "v2 - important");

        // Once B has seen the latest text, the delete goes through and becomes a tombstone.
        var current = await deviceB.PushAsync(cursor: staleDelete.Cursor, notes: [DeleteOf(kept)]);
        var deleteResult = current.Results!.Notes.For(original.Id);
        Assert.Equal(ChangeStatus.Ok, deleteResult.Status);
        Assert.True(deleteResult.Rev > kept.Rev);
        Assert.Null(await deviceB.FindNoteAsync(original.Id));
        var tombstone = (await deviceA.PullAsync(since: edited.Cursor)).Notes.Single(n => n.Id == original.Id);
        Assert.True(tombstone.Deleted);
    }

    [Fact]
    public async Task Deleting_with_a_stale_base_succeeds_when_only_other_fields_changed()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var original = (await deviceA.PushNotesAsync(NewNote("Old idea", "text"))).Notes.Single();
        var pin = EditOf(original, SyncFields.Pinned);
        pin.Pinned = true;
        await deviceA.PushNotesAsync(pin);

        var result = (await deviceB.PushNotesAsync(DeleteOf(original))).Results!.Notes.For(original.Id);

        Assert.Equal(ChangeStatus.Ok, result.Status);
        Assert.Null(await deviceA.FindNoteAsync(original.Id));
    }

    [Fact]
    public async Task A_delete_conflict_sends_the_server_version_back_even_when_the_client_cursor_is_ahead()
    {
        // The browser client ignores pulled changes for records that have unpushed local edits, but still
        // advances its cursor (applyPull in sync.js). So a device can hold a pending delete based on an old
        // revision while its cursor is already past the server's newer text - e.g. when the delete goes out in a
        // second push batch. On "conflict" the client clears the pending delete, adopts result.rev and relies on
        // "the server's version arriving with the pull" (sync.js applyResults).
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var original = (await deviceA.PushNotesAsync(NewNote("Draft", "v1"))).Notes.Single();
        var edit = EditOf(original, SyncFields.Content);
        edit.Content = "v2";
        await deviceA.PushNotesAsync(edit);

        var latest = await deviceB.PullAsync();               // cursor now past "v2"
        var response = await deviceB.PushAsync(cursor: latest.Cursor, notes: [DeleteOf(original)]);

        var result = response.Results!.Notes.For(original.Id);
        Assert.Equal(ChangeStatus.Conflict, result.Status);
        // The server kept "v2", so it has to send it back; otherwise the device keeps showing "v1" (now labelled
        // with the latest revision) and no later pull ever corrects it.
        Assert.Contains(response.Notes, n => n.Id == original.Id && n.Content == "v2");
    }

    [Fact]
    public async Task Editing_a_note_another_device_deleted_brings_it_back()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var binder = NewBinder("Projects");
        var created = await deviceA.PushAsync(notes: [NewNote("Keep me", "v1", binderId: binder.Id)], binders: [binder]);
        var original = created.Notes.Single();
        var deleted = await deviceA.PushNotesAsync(DeleteOf(original));
        var tombstoneRev = deleted.Results!.Notes.For(original.Id).Rev;

        var edit = EditOf(original, SyncFields.Content);
        edit.Content = "still needed";
        var result = (await deviceB.PushNotesAsync(edit)).Results!.Notes.For(original.Id);

        Assert.Equal(ChangeStatus.Ok, result.Status);
        Assert.True(result.Rev > tombstoneRev);
        var revived = await deviceA.FindNoteAsync(original.Id);
        Assert.NotNull(revived);
        Assert.False(revived.Deleted);
        Assert.Equal("still needed", revived.Content);
        Assert.Equal("Keep me", revived.Title);
        Assert.Equal(binder.Id, revived.BinderId);
    }

    [Fact]
    public async Task Editing_a_binder_another_device_deleted_brings_it_back()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var binder = NewBinder("Hobbies", color: "red");
        await deviceA.PushAsync(binders: [binder]);
        var delete = NewBinder("Hobbies", id: binder.Id);
        delete.Deleted = true;
        await deviceA.PushAsync(binders: [delete]);

        var rename = NewBinder("Hobbies & sport", id: binder.Id, color: "orange");
        rename.BaseRev = 1;
        rename.Fields = [SyncFields.Name];
        var result = (await deviceB.PushAsync(binders: [rename])).Results!.Binders.For(binder.Id);

        Assert.Equal(ChangeStatus.Ok, result.Status);
        var revived = Assert.Single((await deviceA.PullAsync()).Binders);
        Assert.False(revived.Deleted);
        Assert.Equal("Hobbies & sport", revived.Name);
        Assert.Equal("orange", revived.Color); // a revived binder takes all of this device's values
    }

    // ------------------------------------------------------------------------------- Binders

    [Fact]
    public async Task Deleting_a_binder_unfiles_its_notes_on_every_device()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        var projects = NewBinder("Projects");
        var personal = NewBinder("Personal");
        var n1 = NewNote("n1", binderId: projects.Id);
        var n2 = NewNote("n2", binderId: projects.Id);
        var n3 = NewNote("n3", binderId: personal.Id);
        var created = await deviceA.PushAsync(notes: [n1, n2, n3], binders: [projects, personal]);

        var delete = NewBinder("Projects", id: projects.Id);
        delete.Deleted = true;
        var response = await deviceA.PushAsync(cursor: created.Cursor, binders: [delete]);
        Assert.Equal(ChangeStatus.Ok, response.Results!.Binders.For(projects.Id).Status);

        // The other device learns about all of it with its next incremental pull.
        var pull = await deviceB.PullAsync(since: created.Cursor);
        Assert.True(pull.Binders.Single(b => b.Id == projects.Id).Deleted);
        var unfiled = pull.Notes.ToDictionary(n => n.Id);
        Assert.Equal(new HashSet<Guid> { n1.Id, n2.Id }, unfiled.Keys.ToHashSet());
        Assert.All(unfiled.Values, n =>
        {
            Assert.Null(n.BinderId);
            Assert.False(n.Deleted);
            Assert.True(n.Rev > created.Cursor);
        });
        Assert.Equal(personal.Id, (await deviceB.FindNoteAsync(n3.Id))!.BinderId);
    }

    [Fact]
    public async Task Notes_cannot_reference_missing_or_deleted_binders()
    {
        var client = await NewUserClientAsync();
        var gone = NewBinder("Gone");
        var live = NewBinder("Live");
        var inLive = NewNote("in live", binderId: live.Id);
        await client.PushAsync(binders: [gone, live], notes: [inLive]);
        var deleteGone = NewBinder("Gone", id: gone.Id);
        deleteGone.Deleted = true;
        await client.PushAsync(binders: [deleteGone]);

        var missing = NewNote("missing binder", binderId: Guid.NewGuid());
        var deleted = NewNote("deleted binder", binderId: gone.Id);
        var response = await client.PushNotesAsync(missing, deleted);
        Assert.All(response.Results!.Notes, r => Assert.Equal(ChangeStatus.Ok, r.Status));

        var notes = (await client.PullAllAsync()).Notes;
        Assert.Null(notes[missing.Id].BinderId);
        Assert.Null(notes[deleted.Id].BinderId);

        // Moving an existing note into a deleted binder leaves it unfiled as well.
        var move = EditOf(notes[inLive.Id], SyncFields.BinderId);
        move.BinderId = gone.Id;
        await client.PushNotesAsync(move);
        Assert.Null((await client.FindNoteAsync(inLive.Id))!.BinderId);
    }

    [Fact]
    public async Task Binders_need_a_name_and_long_names_are_shortened()
    {
        var client = await NewUserClientAsync();
        var unnamed = NewBinder("   ");
        var longName = NewBinder(new string('b', 100));

        var response = await client.PushAsync(binders: [unnamed, longName]);

        var rejected = response.Results!.Binders.For(unnamed.Id);
        Assert.Equal(ChangeStatus.Rejected, rejected.Status);
        Assert.Equal(0, rejected.Rev);
        Assert.False(string.IsNullOrEmpty(rejected.Error));
        var stored = Assert.Single((await client.PullAsync()).Binders);
        Assert.Equal(longName.Id, stored.Id);
        Assert.Equal(new string('b', NoteLimits.MaxBinderNameLength), stored.Name);

        // Renaming to an empty name keeps the existing name.
        var rename = NewBinder("", id: longName.Id);
        rename.Fields = [SyncFields.Name];
        await client.PushAsync(binders: [rename]);
        Assert.Equal(stored.Name, Assert.Single((await client.PullAsync()).Binders).Name);
    }

    // ------------------------------------------------------------------------------- Validation and normalisation

    [Fact]
    public async Task Tags_are_trimmed_deduplicated_stripped_of_hashes_and_capped_at_30()
    {
        var client = await NewUserClientAsync();
        var tags = new List<string> { "  #Work  ", "work", "##Home", "", "   ", "#", "a,b", "multi   word\tspaced", new string('x', 60) };
        tags.AddRange(Enumerable.Range(1, 40).Select(i => $"tag{i}"));

        var note = NewNote("Tagged", tags: tags);
        await client.PushNotesAsync(note);
        var stored = (await client.FindNoteAsync(note.Id))!.Tags;

        Assert.Equal(NoteLimits.MaxTags, stored.Count);
        Assert.Equal(new[] { "Work", "Home", "ab", "multi word spaced", new string('x', NoteLimits.MaxTagLength) }, stored.Take(5));
        Assert.Equal("tag25", stored[^1]);
        Assert.Equal(stored.Count, stored.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("red", "red")]
    [InlineData("gray", "gray")]
    [InlineData("Red", null)]
    [InlineData("magenta", null)]
    [InlineData("#ff0000", null)]
    [InlineData("", null)]
    public async Task Note_and_binder_colours_must_come_from_the_palette(string color, string? expected)
    {
        var client = await NewUserClientAsync();
        var binder = NewBinder("Coloured", color: color);
        var note = NewNote("Coloured");
        note.Color = color;

        await client.PushAsync(notes: [note], binders: [binder]);

        var pull = await client.PullAsync();
        Assert.Equal(expected, Assert.Single(pull.Notes).Color);
        Assert.Equal(expected, Assert.Single(pull.Binders).Color);
    }

    [Theory]
    [InlineData("bi-house-door", "bi-house-door")]
    [InlineData("  bi-star  ", "bi-star")]
    [InlineData("bi-", null)]
    [InlineData("bi-House", null)]
    [InlineData("bi-x\" onload=\"alert(1)", null)]
    [InlineData("\U0001F4DD", "\U0001F4DD")]
    [InlineData("<b>", null)]
    [InlineData("a&b", null)]
    [InlineData("it's", null)]
    [InlineData("0123456789abcdefg", null)] // 17 characters: too long for a symbol
    [InlineData("   ", null)]
    public async Task Icons_must_be_bootstrap_icon_names_or_short_symbols(string icon, string? expected)
    {
        var client = await NewUserClientAsync();
        var note = NewNote("With icon");
        note.Icon = icon;
        var binder = NewBinder("With icon", icon: icon);

        await client.PushAsync(notes: [note], binders: [binder]);

        var pull = await client.PullAsync();
        Assert.Equal(expected, Assert.Single(pull.Notes).Icon);
        Assert.Equal(expected, Assert.Single(pull.Binders).Icon);
    }

    [Fact]
    public async Task Titles_are_cleaned_of_control_characters_and_shortened()
    {
        var client = await NewUserClientAsync();
        var messy = NewNote("  Line1\nLine2\tTab\u0007  ");
        var huge = NewNote(new string('t', 400));

        await client.PushNotesAsync(messy, huge);

        var notes = (await client.PullAllAsync()).Notes;
        Assert.Equal("Line1 Line2 Tab", notes[messy.Id].Title);
        Assert.Equal(new string('t', NoteLimits.MaxTitleLength), notes[huge.Id].Title);
    }

    [Fact]
    public async Task Notes_longer_than_two_million_characters_are_rejected()
    {
        var client = await NewUserClientAsync();
        var tooBig = NewNote("Too big", new string('a', NoteLimits.MaxContentLength + 1));
        var small = NewNote("Small", "fine");

        var response = await client.PushNotesAsync(tooBig, small);

        var rejected = response.Results!.Notes.For(tooBig.Id);
        Assert.Equal(ChangeStatus.Rejected, rejected.Status);
        Assert.Equal(0, rejected.Rev);
        Assert.False(string.IsNullOrEmpty(rejected.Error));
        Assert.Equal(ChangeStatus.Ok, response.Results.Notes.For(small.Id).Status);
        Assert.Equal(new[] { small.Id }, (await client.PullAsync()).Notes.Select(n => n.Id));

        // Growing an existing note past the limit is rejected and the stored text is left alone.
        var grow = EditOf(response.Notes.Single(), SyncFields.Content);
        grow.Content = tooBig.Content;
        Assert.Equal(ChangeStatus.Rejected, (await client.PushNotesAsync(grow)).Results!.Notes.For(small.Id).Status);
        Assert.Equal("fine", (await client.FindNoteAsync(small.Id))!.Content);
    }

    [Fact]
    public async Task Notes_of_exactly_two_million_characters_are_accepted()
    {
        var client = await NewUserClientAsync();
        var atLimit = NewNote("At the limit", new string('b', NoteLimits.MaxContentLength));

        var response = await client.PushNotesAsync(atLimit);

        Assert.Equal(ChangeStatus.Ok, response.Results!.Notes.For(atLimit.Id).Status);
        Assert.Equal(NoteLimits.MaxContentLength, (await client.FindNoteAsync(atLimit.Id))!.Content.Length);
    }

    [Fact]
    public async Task A_push_with_more_than_500_changes_is_a_bad_request_and_applies_nothing()
    {
        var client = await NewUserClientAsync();
        var notes = Enumerable.Range(0, 300).Select(i => NewNote($"n{i}")).ToList();
        var binders = Enumerable.Range(0, 201).Select(i => NewBinder($"b{i}")).ToList();

        var response = await client.PostSyncAsync(new SyncPushRequest { Notes = notes, Binders = binders });

        var (_, detail) = await response.ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Contains("500", detail);
        var pull = await client.PullAsync();
        Assert.Empty(pull.Notes);
        Assert.Empty(pull.Binders);

        // Exactly 500 changes are fine.
        var ok = await client.PushAsync(notes: notes, binders: binders.Take(200));
        Assert.Equal(500, ok.Results!.Notes.Count + ok.Results.Binders.Count);
        Assert.All(ok.Results.Notes.Concat(ok.Results.Binders), r => Assert.Equal(ChangeStatus.Ok, r.Status));
    }

    [Fact]
    public async Task Timestamps_from_the_future_or_unset_are_replaced_by_the_server_time()
    {
        var client = await NewUserClientAsync();
        var future = NewNote("future");
        future.CreatedAt = future.UpdatedAt = NowMs() + 3_600_000;
        future.TrashedAt = future.CreatedAt;
        var unset = NewNote("unset");
        unset.CreatedAt = 0;
        unset.UpdatedAt = -5;
        unset.TrashedAt = 0;
        var skewed = NewNote("slightly ahead");
        skewed.CreatedAt = NowMs() + 60_000; // within the five-minute clock skew allowance
        var past = NewNote("imported");
        past.CreatedAt = 1_000_000_000_000;  // 2001
        past.TrashedAt = 1_500_000_000_000;

        var before = NowMs();
        var response = await client.PushNotesAsync(future, unset, skewed, past);
        var after = NowMs();

        var notes = response.Notes.ToDictionary(n => n.Id);
        Assert.InRange(notes[future.Id].CreatedAt, before, after);
        Assert.InRange(notes[future.Id].UpdatedAt, before, after);
        Assert.InRange(notes[future.Id].TrashedAt!.Value, before, after);
        Assert.InRange(notes[unset.Id].CreatedAt, before, after);
        Assert.InRange(notes[unset.Id].UpdatedAt, before, after);
        Assert.Null(notes[unset.Id].TrashedAt);
        Assert.Equal(skewed.CreatedAt, notes[skewed.Id].CreatedAt);
        Assert.Equal(past.CreatedAt, notes[past.Id].CreatedAt);
        Assert.Equal(past.TrashedAt, notes[past.Id].TrashedAt);
    }

    [Fact]
    public async Task Archive_trash_and_sort_order_round_trip()
    {
        var client = await NewUserClientAsync();
        var original = (await client.PushNotesAsync(NewNote("Lifecycle"))).Notes.Single();

        var archive = EditOf(original, SyncFields.Archived, SyncFields.SortOrder);
        archive.Archived = true;
        archive.SortOrder = -3.25;
        await client.PushNotesAsync(archive);
        var archived = (await client.FindNoteAsync(original.Id))!;
        Assert.True(archived.Archived);
        Assert.Equal(-3.25, archived.SortOrder);

        var trashedAt = NowMs() - 1000;
        var trash = EditOf(archived, SyncFields.TrashedAt);
        trash.TrashedAt = trashedAt;
        await client.PushNotesAsync(trash);
        var trashed = (await client.FindNoteAsync(original.Id))!;
        Assert.Equal(trashedAt, trashed.TrashedAt);

        var restore = EditOf(trashed, SyncFields.TrashedAt, SyncFields.Archived);
        restore.TrashedAt = null;
        restore.Archived = false;
        await client.PushNotesAsync(restore);
        var restored = (await client.FindNoteAsync(original.Id))!;
        Assert.Null(restored.TrashedAt);
        Assert.False(restored.Archived);
        Assert.Equal(original.UpdatedAt, restored.UpdatedAt); // only text edits change "updated"
    }

    // ------------------------------------------------------------------------------- Preferences

    [Fact]
    public async Task Preferences_round_trip_between_devices_and_the_last_writer_wins()
    {
        var (deviceA, deviceB) = await TwoDevicesAsync();
        using var prefsA = JsonDocument.Parse("""{"theme":"dark","sort":"updated","view":{"mode":"grid"}}""");
        var first = await deviceA.PushAsync(new SyncPushRequest { Preferences = prefsA.RootElement.Clone() });
        Assert.NotNull(first.Preferences);
        Assert.Equal("dark", first.Preferences.Data.GetProperty("theme").GetString());
        Assert.Equal(first.Cursor, first.Preferences.Rev);

        var onB = await deviceB.PullAsync();
        Assert.Equal(first.Preferences.Rev, onB.Preferences!.Rev);
        Assert.Equal("grid", onB.Preferences.Data.GetProperty("view").GetProperty("mode").GetString());

        using var prefsB = JsonDocument.Parse("""{"theme":"light"}""");
        var second = await deviceB.PushAsync(new SyncPushRequest { Cursor = onB.Cursor, Preferences = prefsB.RootElement.Clone() });
        Assert.True(second.Preferences!.Rev > first.Preferences.Rev);

        // Replaced as a whole (not merged) on the other device.
        var onA = await deviceA.PullAsync(since: first.Cursor);
        Assert.Equal("""{"theme":"light"}""", onA.Preferences!.Data.GetRawText());
        // Unchanged preferences are not sent again.
        Assert.Null((await deviceA.PullAsync(since: onA.Cursor)).Preferences);

        // Anything but a JSON object is ignored.
        using var array = JsonDocument.Parse("[1,2]");
        var ignored = await deviceA.PushAsync(new SyncPushRequest { Cursor = onA.Cursor, Preferences = array.RootElement.Clone() });
        Assert.Null(ignored.Preferences);
        Assert.Equal("light", (await deviceA.PullAsync()).Preferences!.Data.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Oversized_preferences_reject_the_whole_push()
    {
        var client = await NewUserClientAsync();
        using var big = JsonDocument.Parse(JsonSerializer.Serialize(new { blob = new string('x', NoteLimits.MaxPreferencesLength) }));
        var note = NewNote("in the same push");

        var response = await client.PostSyncAsync(new SyncPushRequest { Notes = [note], Preferences = big.RootElement.Clone() });

        await response.ReadProblemAsync(HttpStatusCode.BadRequest);
        var pull = await client.PullAsync();
        Assert.Empty(pull.Notes);
        Assert.Null(pull.Preferences);
        Assert.Equal(0, pull.Cursor);
    }

    // ------------------------------------------------------------------------------- Paging and reset

    [Fact]
    public async Task Pulls_page_through_changes_with_limit_and_hasMore()
    {
        var client = await NewUserClientAsync();
        var notes = Enumerable.Range(0, 25).Select(i => NewNote($"n{i:00}")).ToList();
        var push = await client.PushAsync(notes: notes);

        var seen = new List<Guid>();
        var pageSizes = new List<int>();
        long cursor = 0;
        for (var i = 0; i < 10; i++)
        {
            var page = await client.PullAsync(cursor, limit: 10);
            pageSizes.Add(page.Notes.Count);
            seen.AddRange(page.Notes.Select(n => n.Id));
            Assert.Equal(page.HasMore ? page.Notes[^1].Rev : push.Cursor, page.Cursor);
            cursor = page.Cursor;
            if (!page.HasMore) break;
        }

        Assert.Equal(new[] { 10, 10, 5 }, pageSizes);
        Assert.Equal(notes.Select(n => n.Id), seen); // every note exactly once, in revision order
        Assert.Equal(push.Cursor, cursor);
    }

    [Fact]
    public async Task Large_accounts_are_paged_with_a_default_of_500_and_a_maximum_of_2000_per_pull()
    {
        var client = await NewUserClientAsync();
        long cursor = 0;
        var ids = new List<Guid>();
        foreach (var batch in Enumerable.Range(0, 2100).Select(i => NewNote($"note {i}")).Chunk(NoteLimits.MaxChangesPerPush))
        {
            ids.AddRange(batch.Select(n => n.Id));
            cursor = (await client.PushAsync(cursor: cursor, notes: batch)).Cursor;
        }

        var byDefault = await client.PullAsync();
        Assert.Equal(SyncService.DefaultPullLimit, byDefault.Notes.Count);
        Assert.True(byDefault.HasMore);

        var capped = await client.PullAsync(limit: 100_000);
        Assert.Equal(SyncService.MaxPullLimit, capped.Notes.Count);
        Assert.True(capped.HasMore);

        var atLeastOne = await client.PullAsync(limit: 0);
        Assert.Single(atLeastOne.Notes);
        Assert.True(atLeastOne.HasMore);

        var all = await client.PullAllAsync();
        Assert.Equal(ids.ToHashSet(), all.Notes.Keys.ToHashSet());
        Assert.Equal(cursor, all.Cursor);
    }

    [Fact]
    public async Task Very_large_notes_are_split_across_pulls_by_size()
    {
        var client = await NewUserClientAsync();
        var big = Enumerable.Range(0, 5).Select(i => NewNote($"big {i}", new string((char)('a' + i), 1_000_000))).ToList();

        var push = await client.PushAsync(notes: big);
        Assert.All(push.Results!.Notes, r => Assert.Equal(ChangeStatus.Ok, r.Status));

        // About 8 MB per response: four 1,000,000-character notes fit, the fifth goes to the next page.
        var first = await client.PullAsync();
        Assert.Equal(4, first.Notes.Count);
        Assert.True(first.HasMore);
        Assert.Equal(first.Notes[^1].Rev, first.Cursor);
        var second = await client.PullAsync(first.Cursor);
        Assert.Equal(big[4].Id, Assert.Single(second.Notes).Id);
        Assert.False(second.HasMore);
    }

    [Fact]
    public async Task A_client_cursor_ahead_of_the_server_gets_a_full_reset()
    {
        var client = await NewUserClientAsync();
        var keep = NewNote("keep");
        var gone = NewNote("gone");
        var created = await client.PushNotesAsync(keep, gone);
        var deleted = await client.PushAsync(cursor: created.Cursor, notes: [DeleteOf(created.Notes.Single(n => n.Id == gone.Id))]);

        // e.g. the server was restored from a backup older than the client's last sync
        var ahead = await client.PullAsync(since: deleted.Cursor + 1000);
        Assert.True(ahead.Reset);
        Assert.Equal(new[] { keep.Id }, ahead.Notes.Select(n => n.Id)); // a full download: no tombstones
        Assert.Equal(deleted.Cursor, ahead.Cursor);

        Assert.True((await client.PullAsync(since: -1)).Reset);

        var offline = NewNote("written offline");
        var push = await client.PushAsync(cursor: deleted.Cursor + 1000, notes: [offline]);
        Assert.True(push.Reset);
        Assert.Equal(ChangeStatus.Ok, push.Results!.Notes.For(offline.Id).Status);
        Assert.Equal(new HashSet<Guid> { keep.Id, offline.Id }, push.Notes.Select(n => n.Id).ToHashSet());

        var normal = await client.PullAsync(since: push.Cursor);
        Assert.False(normal.Reset);
        Assert.Empty(normal.Notes);
    }

    [Fact]
    public async Task Sync_requires_a_signed_in_session()
    {
        var anonymous = factory.CreateAnonymousClient();

        await anonymous.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await anonymous.PostSyncAsync(new SyncPushRequest()).AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
