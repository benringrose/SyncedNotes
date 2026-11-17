using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Sync;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>Every account must only ever see and change its own data.</summary>
public class UserIsolationTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private async Task<(TestUser User, HttpClient Client)> NewUserAsync()
    {
        var user = await factory.CreateUserAsync();
        return (user, factory.CreateClientFor(user));
    }

    private async Task<string> CreateTokenAsync(HttpClient session) =>
        (await (await session.PostJsonAsync("/api/account/tokens", new { name = "test" })).ReadJsonAsync()).Str("token");

    private HttpClient TokenClient(string token)
    {
        var client = factory.CreateAnonymousClient(csrfHeader: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task A_pull_only_returns_the_callers_own_data()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var binder = NewBinder("Alice's binder");
        var diary = NewNote("Alice's diary", "secret", binderId: binder.Id);
        var scrap = NewNote("Alice's scrap");
        using var prefs = JsonDocument.Parse("""{"theme":"dark"}""");
        var created = await alice.PushAsync(new SyncPushRequest
        {
            Binders = [binder],
            Notes = [diary, scrap],
            Preferences = prefs.RootElement.Clone(),
        });
        await alice.PushAsync(cursor: created.Cursor, notes: [DeleteOf(created.Notes.Single(n => n.Id == scrap.Id))]);

        foreach (var since in new long[] { 0, 1, 2 })
        {
            var bobs = await bob.PullAsync(since);
            Assert.Empty(bobs.Notes);
            Assert.Empty(bobs.Binders);
            Assert.Null(bobs.Preferences);
        }

        // Bob's own change sequence is independent and his pushes only ever return his own data.
        var bobPush = await bob.PushNotesAsync(NewNote("Bob's note"));
        Assert.Equal(1, bobPush.Cursor);
        Assert.Equal(new[] { "Bob's note" }, bobPush.Notes.Select(n => n.Title));
        Assert.Empty(bobPush.Binders);
        Assert.Null(bobPush.Preferences);
    }

    [Fact]
    public async Task Pushing_changes_with_another_users_ids_is_rejected_and_changes_nothing()
    {
        var (aliceUser, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var binder = NewBinder("Private", color: "red");
        var note = NewNote("Diary", "original", binderId: binder.Id);
        var aliceView = await alice.PushAsync(notes: [note], binders: [binder]);
        var stolen = aliceView.Notes.Single();

        var overwrite = EditOf(stolen, SyncFields.Title, SyncFields.Content, SyncFields.BinderId);
        overwrite.Title = "pwned";
        overwrite.Content = "pwned";
        overwrite.BinderId = null;
        var renameBinder = NewBinder("pwned", id: binder.Id);
        var first = await bob.PushAsync(notes: [overwrite], binders: [renameBinder]);
        Assert.Equal(ChangeStatus.Rejected, first.Results!.Notes.For(stolen.Id).Status);
        Assert.Equal(ChangeStatus.Rejected, first.Results.Binders.For(binder.Id).Status);

        var deleteBinder = NewBinder("Private", id: binder.Id);
        deleteBinder.Deleted = true;
        var second = await bob.PushAsync(notes: [DeleteOf(stolen)], binders: [deleteBinder]);
        Assert.Equal(ChangeStatus.Rejected, second.Results!.Notes.For(stolen.Id).Status);
        Assert.Equal(ChangeStatus.Rejected, second.Results.Binders.For(binder.Id).Status);

        // A brand-new note can't claim an id that is already in use either.
        var squat = (await bob.PushNotesAsync(NewNote("squatter", id: stolen.Id))).Results!.Notes.For(stolen.Id);
        Assert.Equal(ChangeStatus.Rejected, squat.Status);
        Assert.Equal(0, squat.Rev);

        // Alice's data is untouched: nothing changed since her last sync and her note is still in her binder.
        var aliceAfter = await alice.PullAsync(since: aliceView.Cursor);
        Assert.Empty(aliceAfter.Notes);
        Assert.Empty(aliceAfter.Binders);
        Assert.Equal(aliceView.Cursor, aliceAfter.Cursor);
        var full = await alice.PullAsync();
        var aliceNote = Assert.Single(full.Notes);
        Assert.Equal(("Diary", "original", (Guid?)binder.Id), (aliceNote.Title, aliceNote.Content, aliceNote.BinderId));
        var aliceBinder = Assert.Single(full.Binders);
        Assert.Equal(("Private", false), (aliceBinder.Name, aliceBinder.Deleted));
        await factory.WithDbAsync(async db =>
        {
            var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == stolen.Id);
            Assert.Equal(aliceUser.Id, row.UserId);
            Assert.False(row.IsDeleted);
        });

        // ...and Bob got nothing out of it.
        var bobs = await bob.PullAsync();
        Assert.Empty(bobs.Notes);
        Assert.Empty(bobs.Binders);
    }

    [Fact]
    public async Task Notes_cannot_be_filed_into_another_users_binder()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var aliceBinder = NewBinder("Alice only");
        await alice.PushAsync(binders: [aliceBinder]);

        var intruder = NewNote("Bob's note", binderId: aliceBinder.Id);
        var result = (await bob.PushNotesAsync(intruder)).Results!.Notes.For(intruder.Id);

        Assert.Equal(ChangeStatus.Ok, result.Status);
        Assert.Null((await bob.FindNoteAsync(intruder.Id))!.BinderId);

        // Deleting Alice's binder later doesn't touch Bob's notes.
        var delete = NewBinder("Alice only", id: aliceBinder.Id);
        delete.Deleted = true;
        var bobCursor = (await bob.PullAsync()).Cursor;
        await alice.PushAsync(binders: [delete]);
        Assert.Empty((await bob.PullAsync(since: bobCursor)).Notes);
    }

    [Fact]
    public async Task Attachments_are_only_visible_to_their_owner()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var id = Guid.NewGuid();
        var png = Png();
        await alice.UploadAsync(id, png).AssertStatusAsync(HttpStatusCode.OK);

        // Bob can't download it, even if one of his notes links to it...
        await bob.PushNotesAsync(NewNote("Look", $"![](/attachments/{id})"));
        await (await bob.GetAsync($"/attachments/{id}")).ReadProblemAsync(HttpStatusCode.NotFound);

        // ...and can't overwrite it by uploading with the same id.
        await (await bob.UploadAsync(id, Png(padding: 100))).ReadProblemAsync(HttpStatusCode.Conflict);

        var download = await alice.GetAsync($"/attachments/{id}");
        await download.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(png, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Integration_tokens_only_see_their_owners_notes_and_binders()
    {
        var (_, alice) = await NewUserAsync();
        var (bobUser, bob) = await NewUserAsync();
        var aliceBinder = NewBinder("Finance");
        await alice.PushAsync(binders: [aliceBinder], notes: [NewNote("Alice salary", "shared keyword", binderId: aliceBinder.Id)]);
        await bob.PushAsync(binders: [NewBinder("Bob binder")], notes: [NewNote("Bob plans", "shared keyword")]);
        var bobToken = TokenClient(await CreateTokenAsync(bob));

        var search = await bobToken.GetJsonAsync("/api/v1/quick/notes?q=shared");
        Assert.Equal(new[] { "Bob plans" }, search.EnumerateArray().Select(n => n.Str("title")));
        var everything = await bobToken.GetJsonAsync("/api/v1/quick/notes");
        Assert.Equal(new[] { "Bob plans" }, everything.EnumerateArray().Select(n => n.Str("title")));
        var binders = await bobToken.GetJsonAsync("/api/v1/quick/binders");
        Assert.Equal(new[] { "Bob binder" }, binders.EnumerateArray().Select(b => b.Str("name")));

        // Quick-adding into "Finance" (Alice's binder name) leaves Bob's note unfiled and Alice unaffected.
        var aliceCursor = (await alice.PullAsync()).Cursor;
        var created = await (await bobToken.PostJsonAsync("/api/v1/quick/notes", new { content = "mine", binder = "Finance" }))
            .ReadJsonAsync(HttpStatusCode.Created);
        var createdId = created.GetProperty("id").GetGuid();
        Assert.Null((await bob.FindNoteAsync(createdId))!.BinderId);
        Assert.Empty((await alice.PullAsync(since: aliceCursor)).Notes);
        await factory.WithDbAsync(async db =>
            Assert.Equal(bobUser.Id, (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == createdId)).UserId));
    }

    [Fact]
    public async Task Tokens_can_only_be_listed_and_revoked_by_their_owner()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var created = await (await alice.PostJsonAsync("/api/account/tokens", new { name = "alice-cli" })).ReadJsonAsync();
        var tokenId = created.GetProperty("id").GetGuid();

        Assert.Empty((await bob.GetJsonAsync("/api/account/tokens")).EnumerateArray());
        await (await bob.DeleteAsync($"/api/account/tokens/{tokenId}")).ReadProblemAsync(HttpStatusCode.NotFound);

        await TokenClient(created.Str("token")).GetAsync("/api/v1/quick/binders").AssertStatusAsync(HttpStatusCode.OK);
        Assert.Single((await alice.GetJsonAsync("/api/account/tokens")).EnumerateArray());
    }

    [Fact]
    public async Task Cookie_sessions_and_integration_tokens_are_not_interchangeable()
    {
        var (_, bob) = await NewUserAsync();
        var token = TokenClient(await CreateTokenAsync(bob));

        // A token can't be used for the browser APIs...
        await token.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await token.GetAsync("/api/account/tokens").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await token.GetAsync("/api/export").AssertStatusAsync(HttpStatusCode.Unauthorized);

        // ...and a browser session can't be used for the token API.
        var quick = await bob.GetAsync("/api/v1/quick/notes");
        await quick.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer", quick.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task Each_user_only_exports_their_own_notes()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        await alice.PushNotesAsync(NewNote("Alice secret", "do not leak"));
        await bob.PushNotesAsync(NewNote("Bob note", "hello"));

        var zip = await ExportTests.DownloadExportAsync(bob);

        var notes = zip.Paths.Where(k => k.StartsWith("notes/")).ToList();
        Assert.Equal(new[] { "notes/Unfiled/Bob note.md" }, notes);
        Assert.DoesNotContain(zip.Paths, path => zip.Text(path).Contains("do not leak") || zip.Text(path).Contains("Alice"));
    }
}
