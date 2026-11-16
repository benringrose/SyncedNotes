using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Sync;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>Personal access tokens and the integration API (/api/v1/quick/*) they unlock.</summary>
public class ApiTokenTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private HttpClient TokenClient(string? authorization)
    {
        var client = factory.CreateAnonymousClient(csrfHeader: false);
        if (authorization is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        }
        return client;
    }

    private async Task<(TestUser User, HttpClient Session, HttpClient Api)> NewUserWithTokenAsync()
    {
        var user = await factory.CreateUserAsync();
        var session = factory.CreateClientFor(user);
        var created = await (await session.PostJsonAsync("/api/account/tokens", new { name = "PowerToys" })).ReadJsonAsync();
        return (user, session, TokenClient($"Bearer {created.Str("token")}"));
    }

    private static Task<HttpResponseMessage> QuickAddAsync(HttpClient api, object body) => api.PostJsonAsync("/api/v1/quick/notes", body);

    private static async Task<List<JsonElement>> SearchAsync(HttpClient api, string query = "") =>
        (await api.GetJsonAsync($"/api/v1/quick/notes{query}")).EnumerateArray().ToList();

    // ------------------------------------------------------------------------------- Token management

    [Fact]
    public async Task Tokens_can_be_created_listed_used_and_revoked()
    {
        var user = await factory.CreateUserAsync();
        var session = factory.CreateClientFor(user);

        var before = NowMs();
        var created = await (await session.PostJsonAsync("/api/account/tokens", new { name = "  PowerToys  " })).ReadJsonAsync();
        var token = created.Str("token");
        var id = created.GetProperty("id").GetGuid();
        Assert.StartsWith(ApiTokenDefaults.TokenPrefix, token);
        Assert.InRange(token.Length, 40, 100);
        Assert.Equal(token[..10], created.Str("prefix"));
        Assert.Equal("PowerToys", created.Str("name"));
        Assert.InRange(created.GetProperty("createdAt").GetInt64(), before, NowMs());

        // Only a hash is stored.
        var row = await factory.WithDbAsync(db => db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == id));
        Assert.Equal(ApiTokenService.Hash(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);

        var listed = Assert.Single((await session.GetJsonAsync("/api/account/tokens")).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetGuid());
        Assert.Equal(created.Str("prefix"), listed.Str("prefix"));
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("lastUsedAt").ValueKind);
        Assert.False(listed.TryGetProperty("token", out _));
        Assert.False(listed.TryGetProperty("tokenHash", out _));

        var api = TokenClient($"Bearer {token}");
        await api.GetAsync("/api/v1/quick/notes").AssertStatusAsync(HttpStatusCode.OK);
        var used = Assert.Single((await session.GetJsonAsync("/api/account/tokens")).EnumerateArray());
        Assert.InRange(used.GetProperty("lastUsedAt").GetInt64(), before, NowMs());
        // "bearer" is case-insensitive and surrounding whitespace is ignored.
        await TokenClient($"bearer   {token}  ").GetAsync("/api/v1/quick/binders").AssertStatusAsync(HttpStatusCode.OK);

        await session.DeleteAsync($"/api/account/tokens/{id}").AssertStatusAsync(HttpStatusCode.NoContent);
        await (await session.DeleteAsync($"/api/account/tokens/{id}")).ReadProblemAsync(HttpStatusCode.NotFound);
        await api.GetAsync("/api/v1/quick/notes").AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Empty((await session.GetJsonAsync("/api/account/tokens")).EnumerateArray());
    }

    [Theory]
    [InlineData(null, "Integration")]
    [InlineData("", "Integration")]
    [InlineData("   ", "Integration")]
    [InlineData("Raycast", "Raycast")]
    public async Task Token_names_default_to_integration(string? name, string expected)
    {
        var session = factory.CreateClientFor(await factory.CreateUserAsync());

        var created = await (await session.PostJsonAsync("/api/account/tokens", new { name })).ReadJsonAsync();

        Assert.Equal(expected, created.Str("name"));
    }

    [Fact]
    public async Task Token_names_are_capped_at_100_characters_and_each_user_may_have_25_tokens()
    {
        var user = await factory.CreateUserAsync();
        var session = factory.CreateClientFor(user);
        var first = await (await session.PostJsonAsync("/api/account/tokens", new { name = new string('n', 150) })).ReadJsonAsync();
        Assert.Equal(new string('n', 100), first.Str("name"));

        await factory.WithScopeAsync(async sp =>
        {
            var tokens = sp.GetRequiredService<ApiTokenService>();
            for (var i = 1; i < ApiTokenService.MaxTokensPerUser; i++) await tokens.CreateAsync(user.Id, $"t{i}");
        });

        var (title, _) = await (await session.PostJsonAsync("/api/account/tokens", new { name = "one too many" }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("You have too many tokens. Revoke one first.", title);
        Assert.Equal(ApiTokenService.MaxTokensPerUser, (await session.GetJsonAsync("/api/account/tokens")).GetArrayLength());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer not-a-token")]
    [InlineData("Bearer snp_")]
    [InlineData("Bearer snp_doesnotexist0000000000000000000000000000")]
    [InlineData("Bearer SNP_doesnotexist0000000000000000000000000000")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("snp_missing-scheme")]
    public async Task Missing_malformed_or_unknown_tokens_are_unauthorized(string? authorization)
    {
        var api = TokenClient(authorization);

        var response = await api.GetAsync("/api/v1/quick/notes");

        await response.AssertStatusAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        await QuickAddAsync(api, new { title = "nope" }).AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Overlong_tokens_are_rejected_before_any_lookup()
    {
        var api = TokenClient("Bearer snp_" + new string('a', 200));

        await api.GetAsync("/api/v1/quick/binders").AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_management_requires_a_browser_session()
    {
        var anonymous = factory.CreateAnonymousClient();

        await anonymous.GetAsync("/api/account/tokens").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await anonymous.PostJsonAsync("/api/account/tokens", new { name = "x" }).AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------------------- Quick add

    [Fact]
    public async Task Quick_add_creates_a_note_that_syncs_to_the_users_devices()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var work = NewBinder("Work");
        var existing = NewNote("Existing");
        existing.SortOrder = -10;
        var seeded = await session.PushAsync(binders: [work, NewBinder("Home")], notes: [existing]);

        var response = await QuickAddAsync(api, new
        {
            content = "# Groceries\r\n- milk\r\n- eggs",
            binder = "  work ",
            tags = new[] { "#errands", "errands", " shopping " },
            pinned = true,
        });

        var body = await response.ReadJsonAsync(HttpStatusCode.Created);
        var id = body.GetProperty("id").GetGuid();
        var url = body.Str("url");
        Assert.Equal("Groceries", body.Str("title"));
        Assert.Equal($"http://localhost/#/note/{id}", url);
        Assert.EndsWith($"/#/note/{id}", url);
        Assert.Equal(url, response.Headers.Location!.ToString());

        var pull = await session.PullAsync(since: seeded.Cursor);
        var note = Assert.Single(pull.Notes);
        Assert.Equal(id, note.Id);
        Assert.Equal("Groceries", note.Title);
        Assert.Equal("# Groceries\n- milk\n- eggs", note.Content);
        Assert.Equal(work.Id, note.BinderId);
        Assert.Equal(new[] { "errands", "shopping" }, note.Tags);
        Assert.True(note.Pinned);
        Assert.True(note.SortOrder < existing.SortOrder, "quick notes go to the top of the list");
        Assert.True(note.Rev > seeded.Cursor);
        Assert.Equal(note.Rev, pull.Cursor);
    }

    [Fact]
    public async Task Quick_add_derives_a_title_from_the_first_meaningful_line()
    {
        var (_, session, api) = await NewUserWithTokenAsync();

        async Task<string> TitleOf(object request) =>
            (await (await QuickAddAsync(api, request)).ReadJsonAsync(HttpStatusCode.Created)).Str("title");

        Assert.Equal("Explicit title", await TitleOf(new { title = "  Explicit title  ", content = "# Ignored heading" }));
        Assert.Equal("Call Bob about the report", await TitleOf(new { content = "\n   \n> - [ ] 1. Call Bob about the report\nmore" }));
        Assert.Equal(new string('x', 80) + "…", await TitleOf(new { content = new string('x', 100) }));
        Assert.Equal("Quick note", await TitleOf(new { content = "```\n```" }));
        Assert.Equal("Only a title", await TitleOf(new { title = "Only a title" }));
        Assert.Equal(5, (await session.PullAsync()).Notes.Count);
    }

    [Fact]
    public async Task Quick_add_validates_its_input()
    {
        var (_, session, api) = await NewUserWithTokenAsync();

        var (emptyTitle, _) = await (await QuickAddAsync(api, new { })).ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("A note needs a title or some content.", emptyTitle);
        await (await QuickAddAsync(api, new { title = "   ", content = " \n\t " })).ReadProblemAsync(HttpStatusCode.BadRequest);
        var (largeTitle, _) = await (await QuickAddAsync(api, new { title = "big", content = new string('a', NoteLimits.MaxContentLength + 1) }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("The note is too large.", largeTitle);
        Assert.Empty((await session.PullAsync()).Notes);

        // An unknown binder name just leaves the note unfiled.
        var created = await (await QuickAddAsync(api, new { title = "Somewhere", binder = "Nonexistent" })).ReadJsonAsync(HttpStatusCode.Created);
        Assert.Null((await session.FindNoteAsync(created.GetProperty("id").GetGuid()))!.BinderId);
    }

    // ------------------------------------------------------------------------------- Search and binders

    [Fact]
    public async Task Search_matches_title_content_or_tag_and_skips_archived_trashed_and_deleted_notes()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var projects = NewBinder("Projects");
        var title = NewNote("Quarterly report", "numbers", binderId: projects.Id);
        var content = NewNote("Ideas", "Write the REPORT intro");
        var tag = NewNote("Misc", "nothing here", tags: ["report-2026"]);
        var pinned = NewNote("Pinned report");
        pinned.Pinned = true;
        var archived = NewNote("Archived report");
        archived.Archived = true;
        var trashed = NewNote("Trashed report");
        trashed.TrashedAt = NowMs();
        var deleted = NewNote("Deleted report");
        var unrelated = NewNote("Unrelated", "nothing");
        var created = await session.PushAsync(binders: [projects], notes: [title, content, tag, pinned, archived, trashed, deleted, unrelated]);
        await session.PushNotesAsync(DeleteOf(created.Notes.Single(n => n.Id == deleted.Id)));

        var results = await SearchAsync(api, "?q=report");

        var ids = results.Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new HashSet<Guid> { title.Id, content.Id, tag.Id, pinned.Id }, ids.ToHashSet());
        Assert.Equal(pinned.Id, ids[0]); // pinned notes first
        var report = results.Single(r => r.GetProperty("id").GetGuid() == title.Id);
        Assert.Equal("Quarterly report", report.Str("title"));
        Assert.Equal("numbers", report.Str("snippet"));
        Assert.Equal("Projects", report.Str("binder"));
        Assert.Equal($"http://localhost/#/note/{title.Id}", report.Str("url"));
        Assert.False(report.GetProperty("pinned").GetBoolean());
        Assert.Equal(title.UpdatedAt, report.GetProperty("updatedAt").GetInt64());
        Assert.Equal(new[] { "report-2026" }, results.Single(r => r.GetProperty("id").GetGuid() == tag.Id)
            .GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(JsonValueKind.Null, results.Single(r => r.GetProperty("id").GetGuid() == content.Id).GetProperty("binder").ValueKind);

        // Without a query: every live note (up to the limit).
        var all = await SearchAsync(api);
        Assert.Equal(5, all.Count);
        Assert.Equal(2, (await SearchAsync(api, "?limit=2")).Count);
        Assert.Single(await SearchAsync(api, "?limit=0"));
        Assert.Empty(await SearchAsync(api, "?q=zzz-no-match"));
    }

    [Fact]
    public async Task Search_treats_like_wildcards_literally_and_caps_the_page_size()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var percent = NewNote("100% done");
        var underscore = NewNote("snake_case");
        var others = Enumerable.Range(0, 60).Select(i => NewNote($"filler {i}")).ToList();
        await session.PushAsync(notes: [percent, underscore, .. others]);

        Assert.Equal(new[] { percent.Id }, (await SearchAsync(api, "?q=%25")).Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { underscore.Id }, (await SearchAsync(api, "?q=_")).Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal(50, (await SearchAsync(api, "?limit=500")).Count);
        Assert.Equal(20, (await SearchAsync(api)).Count); // default page size
    }

    [Fact]
    public async Task Search_snippets_are_short_plain_text()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var markdown = NewNote("Formatted", "## Heading\n**bold** and [a link](https://example.com) ![img](/attachments/x) `code`");
        var longNote = NewNote("Long", string.Join(' ', Enumerable.Repeat("lorem ipsum", 100)));
        await session.PushNotesAsync(markdown, longNote);

        var results = (await SearchAsync(api)).ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.Str("snippet"));

        Assert.Equal("Heading bold and a link code", results[markdown.Id]);
        Assert.EndsWith("…", results[longNote.Id]);
        Assert.InRange(results[longNote.Id].Length, 100, 161);
    }

    [Fact]
    public async Task Binders_endpoint_lists_live_binders_in_sort_order()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var b = NewBinder("Beta");
        b.SortOrder = 2;
        var a = NewBinder("Alpha");
        a.SortOrder = 1;
        var gone = NewBinder("Gone");
        await session.PushAsync(binders: [b, a, gone]);
        var delete = NewBinder("Gone", id: gone.Id);
        delete.Deleted = true;
        await session.PushAsync(binders: [delete]);

        var binders = (await api.GetJsonAsync("/api/v1/quick/binders")).EnumerateArray().ToList();

        Assert.Equal(new[] { "Alpha", "Beta" }, binders.Select(x => x.Str("name")));
        Assert.Equal(new[] { a.Id, b.Id }, binders.Select(x => x.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_integration_api_is_rate_limited_per_token()
    {
        var (_, session, api) = await NewUserWithTokenAsync();
        var second = await (await session.PostJsonAsync("/api/account/tokens", new { name = "second" })).ReadJsonAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 121; i++) statuses.Add((await api.GetAsync("/api/v1/quick/binders")).StatusCode);

        Assert.All(statuses.Take(120), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[120]);
        await TokenClient($"Bearer {second.Str("token")}").GetAsync("/api/v1/quick/binders").AssertStatusAsync(HttpStatusCode.OK);
    }
}
