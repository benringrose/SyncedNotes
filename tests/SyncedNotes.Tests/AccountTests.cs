using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Services;
using SyncedNotes.Sync;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>Account settings: profile, passkeys, deletion and new-account seeding.</summary>
public class AccountTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    /// <summary>Stores a passkey directly (a real WebAuthn ceremony needs an authenticator).</summary>
    private Task<byte[]> AddPasskeyAsync(TestUser user, string name, DateTimeOffset createdAt, bool backedUp = false) =>
        factory.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var appUser = (await users.FindByIdAsync(user.Id))!;
            var credentialId = Guid.NewGuid().ToByteArray();
            var passkey = new UserPasskeyInfo(
                credentialId, publicKey: new byte[77], createdAt, signCount: 0, transports: ["internal", "hybrid"],
                isUserVerified: true, isBackupEligible: true, isBackedUp: backedUp, attestationObject: [], clientDataJson: [])
            {
                Name = name,
            };
            var result = await users.AddOrUpdatePasskeyAsync(appUser, passkey);
            Assert.True(result.Succeeded);
            return credentialId;
        });

    [Fact]
    public async Task The_display_name_is_trimmed_capped_and_falls_back_to_the_username()
    {
        var user = await factory.CreateUserAsync();
        var client = factory.CreateClientFor(user);

        var profile = await client.GetJsonAsync("/api/account/profile");
        Assert.Equal(user.UserName, profile.Str("userName"));
        Assert.Equal(user.UserName, profile.Str("displayName"));
        Assert.True(profile.GetProperty("createdAt").GetInt64() > 0);

        var updated = await (await client.PutJsonAsync("/api/account/profile", new { displayName = "  Ada Lovelace  " })).ReadJsonAsync();
        Assert.Equal("Ada Lovelace", updated.Str("displayName"));
        Assert.Equal("Ada Lovelace", (await client.GetJsonAsync("/api/account/profile")).Str("displayName"));
        Assert.Equal("Ada Lovelace", (await client.GetJsonAsync("/api/session")).Str("displayName"));

        var capped = await (await client.PutJsonAsync("/api/account/profile", new { displayName = new string('d', 100) })).ReadJsonAsync();
        Assert.Equal(new string('d', 64), capped.Str("displayName"));

        var cleared = await (await client.PutJsonAsync("/api/account/profile", new { displayName = "   " })).ReadJsonAsync();
        Assert.Equal(user.UserName, cleared.Str("displayName"));
    }

    [Fact]
    public async Task Passkeys_can_be_listed_renamed_and_removed_but_never_the_last_one()
    {
        var user = await factory.CreateUserAsync();
        var client = factory.CreateClientFor(user);
        Assert.Empty((await client.GetJsonAsync("/api/account/passkeys")).EnumerateArray());

        var laptopCreated = DateTimeOffset.UtcNow.AddDays(-2);
        var laptop = await AddPasskeyAsync(user, "Laptop", laptopCreated, backedUp: true);
        var phone = await AddPasskeyAsync(user, "Phone", DateTimeOffset.UtcNow.AddDays(-1));
        var laptopId = Base64Url.EncodeToString(laptop);
        var phoneId = Base64Url.EncodeToString(phone);

        var listed = (await client.GetJsonAsync("/api/account/passkeys")).EnumerateArray().ToList();
        Assert.Equal(new[] { laptopId, phoneId }, listed.Select(p => p.Str("id"))); // oldest first
        Assert.Equal("Laptop", listed[0].Str("name"));
        Assert.Equal(laptopCreated.ToUnixTimeMilliseconds(), listed[0].GetProperty("createdAt").GetInt64());
        Assert.True(listed[0].GetProperty("backedUp").GetBoolean());
        Assert.Equal(new[] { "internal", "hybrid" }, listed[0].GetProperty("transports").EnumerateArray().Select(t => t.GetString()));
        var session = await client.GetJsonAsync("/api/session");
        Assert.Equal(new HashSet<string> { laptopId, phoneId }, session.GetProperty("passkeyIds").EnumerateArray().Select(p => p.GetString()!).ToHashSet());

        // Renaming cleans the name; unknown and malformed ids are reported as such.
        var renamed = await (await client.PutJsonAsync($"/api/account/passkeys/{laptopId}", new { name = "  Work laptop\u0007" })).ReadJsonAsync();
        Assert.Equal("Work laptop", renamed.Str("name"));
        Assert.Equal("Passkey", (await (await client.PutJsonAsync($"/api/account/passkeys/{phoneId}", new { name = " " })).ReadJsonAsync()).Str("name"));
        Assert.Equal(60, (await (await client.PutJsonAsync($"/api/account/passkeys/{phoneId}", new { name = new string('p', 80) })).ReadJsonAsync()).Str("name").Length);
        var unknownId = Base64Url.EncodeToString(Guid.NewGuid().ToByteArray());
        await (await client.PutJsonAsync($"/api/account/passkeys/{unknownId}", new { name = "x" })).ReadProblemAsync(HttpStatusCode.NotFound);
        await (await client.PutJsonAsync("/api/account/passkeys/%21%21%21", new { name = "x" })).ReadProblemAsync(HttpStatusCode.BadRequest);

        // Another account can't touch them.
        var other = factory.CreateClientFor(await factory.CreateUserAsync());
        await (await other.PutJsonAsync($"/api/account/passkeys/{laptopId}", new { name = "mine" })).ReadProblemAsync(HttpStatusCode.NotFound);
        await (await other.DeleteAsync($"/api/account/passkeys/{laptopId}")).ReadProblemAsync(HttpStatusCode.NotFound);

        await client.DeleteAsync($"/api/account/passkeys/{phoneId}").AssertStatusAsync(HttpStatusCode.NoContent);
        await (await client.DeleteAsync($"/api/account/passkeys/{phoneId}")).ReadProblemAsync(HttpStatusCode.NotFound);
        await (await client.DeleteAsync("/api/account/passkeys/%21%21%21")).ReadProblemAsync(HttpStatusCode.BadRequest);

        // The last passkey can't be removed, or the account could never be signed in to again.
        var (title, _) = await (await client.DeleteAsync($"/api/account/passkeys/{laptopId}")).ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("You can't remove your only passkey.", title);
        var remaining = Assert.Single((await client.GetJsonAsync("/api/account/passkeys")).EnumerateArray());
        Assert.Equal(("Work laptop", laptopId), (remaining.Str("name"), remaining.Str("id")));
    }

    [Fact]
    public async Task Adding_a_passkey_starts_from_options_for_the_signed_in_user()
    {
        var user = await factory.CreateUserAsync();
        var existing = Base64Url.EncodeToString(await AddPasskeyAsync(user, "Laptop", DateTimeOffset.UtcNow));
        // A cookie-keeping client, so the ceremony state survives between the two calls.
        var browser = factory.CreateBrowserClient();
        browser.DefaultRequestHeaders.Add(TestAuth.UserHeader, user.Id);

        var options = await (await browser.PostJsonAsync("/api/account/passkeys/options", null)).ReadJsonAsync();
        Assert.Equal(user.UserName, options.GetProperty("user").Str("name"));
        Assert.Equal(user.UserName, options.GetProperty("user").Str("displayName"));
        Assert.Contains(existing, options.GetProperty("excludeCredentials").EnumerateArray().Select(c => c.Str("id")));

        var (bogusTitle, _) = await (await browser.PostJsonAsync("/api/account/passkeys", new { credentialJson = """{"id":"bogus"}""", name = "x" }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("The passkey couldn't be added.", bogusTitle);
        var (emptyTitle, _) = await (await browser.PostJsonAsync("/api/account/passkeys", new { credentialJson = "" }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("The browser didn't provide a passkey.", emptyTitle);
        Assert.Single((await browser.GetJsonAsync("/api/account/passkeys")).EnumerateArray());
    }

    [Fact]
    public async Task Deleting_an_account_removes_all_of_its_data_and_nothing_else()
    {
        var alice = await factory.CreateUserAsync();
        var bob = await factory.CreateUserAsync();
        var clients = new Dictionary<TestUser, (HttpClient Session, string Token)>();
        foreach (var user in new[] { alice, bob })
        {
            var session = factory.CreateClientFor(user);
            using var prefs = JsonDocument.Parse("""{"theme":"dark"}""");
            var binder = NewBinder($"{user.UserName} binder");
            await session.PushAsync(new SyncPushRequest
            {
                Binders = [binder],
                Notes = [NewNote($"{user.UserName} note", binderId: binder.Id)],
                Preferences = prefs.RootElement.Clone(),
            });
            await session.UploadAsync(Guid.NewGuid(), Png()).AssertStatusAsync(HttpStatusCode.OK);
            await session.PostJsonAsync("/api/account/recovery-codes", null).AssertStatusAsync(HttpStatusCode.OK);
            var token = (await (await session.PostJsonAsync("/api/account/tokens", new { name = "t" })).ReadJsonAsync()).Str("token");
            clients[user] = (session, token);
        }
        await AddPasskeyAsync(alice, "Laptop", DateTimeOffset.UtcNow);
        var aliceSession = clients[alice].Session;

        var (title, _) = await (await aliceSession.PostJsonAsync("/api/account/delete", new { confirmUserName = alice.UserName.ToUpperInvariant() }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Type your username exactly to confirm.", title);
        await aliceSession.PostJsonAsync("/api/account/delete", new { }).AssertStatusAsync(HttpStatusCode.BadRequest);
        Assert.Single((await aliceSession.PullAsync()).Notes);

        var deleted = await (await aliceSession.PostJsonAsync("/api/account/delete", new { confirmUserName = $" {alice.UserName} " })).ReadJsonAsync();
        Assert.StartsWith("/Account/Login?", deleted.Str("redirect"));
        var page = await factory.CreateAnonymousClient().GetAsync(deleted.Str("redirect"));
        Assert.Contains("Your account and notes have been deleted.", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));

        await factory.WithDbAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == alice.Id));
            Assert.False(await db.Notes.AnyAsync(n => n.UserId == alice.Id));
            Assert.False(await db.Binders.AnyAsync(b => b.UserId == alice.Id));
            Assert.False(await db.Attachments.AnyAsync(a => a.UserId == alice.Id));
            Assert.False(await db.ApiTokens.AnyAsync(t => t.UserId == alice.Id));
            Assert.False(await db.RecoveryCodes.AnyAsync(r => r.UserId == alice.Id));
            Assert.False(await db.UserStates.AnyAsync(s => s.UserId == alice.Id));
            Assert.False(await db.UserPasskeys.AnyAsync(p => p.UserId == alice.Id));

            Assert.Equal(1, await db.Notes.CountAsync(n => n.UserId == bob.Id));
            Assert.Equal(1, await db.Binders.CountAsync(b => b.UserId == bob.Id));
            Assert.Equal(1, await db.Attachments.CountAsync(a => a.UserId == bob.Id));
            Assert.Equal(10, await db.RecoveryCodes.CountAsync(r => r.UserId == bob.Id));
        });

        var aliceToken = factory.CreateAnonymousClient(csrfHeader: false);
        aliceToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clients[alice].Token);
        await aliceToken.GetAsync("/api/v1/quick/notes").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await aliceSession.GetAsync("/api/session").AssertStatusAsync(HttpStatusCode.Unauthorized);

        var bobToken = factory.CreateAnonymousClient(csrfHeader: false);
        bobToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clients[bob].Token);
        Assert.Single((await bobToken.GetJsonAsync("/api/v1/quick/notes")).EnumerateArray());
        var bobPull = await clients[bob].Session.PullAsync();
        Assert.Single(bobPull.Notes);
        Assert.Equal("dark", bobPull.Preferences!.Data.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task New_accounts_are_seeded_with_starter_binders_and_a_welcome_note()
    {
        var user = await factory.CreateUserAsync();

        await factory.WithScopeAsync(sp => sp.GetRequiredService<SeedService>().SeedNewUserAsync(user.Id));

        var pull = await factory.CreateClientFor(user).PullAsync();
        Assert.Equal(new[] { "Work", "Home", "Business", "Sport" }, pull.Binders.OrderBy(b => b.SortOrder).Select(b => b.Name));
        Assert.All(pull.Binders, b =>
        {
            Assert.NotNull(NoteRules.NormalizeColor(b.Color));
            Assert.Equal(b.Icon, NoteRules.NormalizeIcon(b.Icon));
        });
        var welcome = Assert.Single(pull.Notes);
        Assert.Equal("Welcome to SyncedNotes", welcome.Title);
        Assert.True(welcome.Pinned);
        Assert.Null(welcome.BinderId);
        Assert.Equal(new[] { "getting-started" }, welcome.Tags);
        Assert.Contains("Markdown", welcome.Content);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, pull.Binders.Select(b => b.Rev).Append(welcome.Rev).Order());
        Assert.Equal(5, pull.Cursor);
    }
}
