using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SyncedNotes.Auth;
using SyncedNotes.Tests.Infrastructure;

namespace SyncedNotes.Tests;

/// <summary>Auth:Registration (FirstUserOnly, Open, Closed). Each test needs its own fresh server.</summary>
public class RegistrationPolicyTests
{
    private static async Task<bool> RegistrationOpenAsync(HttpClient client) =>
        (await client.GetJsonAsync("/api/auth/status")).GetProperty("registrationOpen").GetBoolean();

    [Fact]
    public async Task First_user_only_registration_closes_once_an_account_exists()
    {
        await using var factory = new SyncedNotesFactory(new FactoryOptions { Registration = "FirstUserOnly" });
        var anonymous = factory.CreateAnonymousClient();

        Assert.True(await RegistrationOpenAsync(anonymous));
        // A brand-new server sends visitors straight to registration.
        var login = await anonymous.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account/Register", login.Headers.Location!.OriginalString);
        var options = await (await anonymous.PostJsonAsync("/api/auth/register/options",
            new { userName = "first.user", displayName = "  First User  " })).ReadJsonAsync();
        Assert.Equal("first.user", options.GetProperty("user").Str("name"));
        Assert.Equal("First User", options.GetProperty("user").Str("displayName"));

        await factory.CreateUserAsync("first.user");

        Assert.False(await RegistrationOpenAsync(anonymous));
        var (title, _) = await (await anonymous.PostJsonAsync("/api/auth/register/options", new { userName = "second.user" }))
            .ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Registration is closed.", title);
        var page = await anonymous.GetAsync("/Account/Login");
        await page.AssertStatusAsync(HttpStatusCode.OK);
        Assert.DoesNotContain("href=\"/Account/Register\"", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Open_registration_accepts_new_accounts_but_not_taken_or_invalid_usernames()
    {
        await using var factory = new SyncedNotesFactory(new FactoryOptions { Registration = "Open" });
        await factory.CreateUserAsync("alice");
        var anonymous = factory.CreateAnonymousClient();

        Assert.True(await RegistrationOpenAsync(anonymous));
        var page = await anonymous.GetAsync("/Account/Login");
        Assert.Contains("href=\"/Account/Register\"", await page.Content.ReadAsStringAsync());

        var options = await (await anonymous.PostJsonAsync("/api/auth/register/options", new { userName = "  bob  " })).ReadJsonAsync();
        Assert.Equal("bob", options.GetProperty("user").Str("name"));
        Assert.Equal("bob", options.GetProperty("user").Str("displayName")); // defaults to the username

        foreach (var taken in new[] { "alice", "ALICE", " Alice " })
        {
            var (title, _) = await (await anonymous.PostJsonAsync("/api/auth/register/options", new { userName = taken }))
                .ReadProblemAsync(HttpStatusCode.Conflict);
            Assert.Equal("That username is already taken.", title);
        }
    }

    [Fact]
    public async Task Usernames_must_be_3_to_64_safe_characters_starting_with_a_letter_or_digit()
    {
        await using var factory = new SyncedNotesFactory(new FactoryOptions { Registration = "Open" });
        var anonymous = factory.CreateAnonymousClient();

        string?[] invalid = [null, "", "ab", "-alice", ".alice", "_alice", "has space", "a/b", "semi;colon", "ünïcode", "tab\tname", new string('a', 65)];
        foreach (var userName in invalid)
        {
            var response = await anonymous.PostJsonAsync("/api/auth/register/options", new { userName });
            var (title, _) = await response.ReadProblemAsync(HttpStatusCode.BadRequest);
            Assert.Equal("Choose a different username.", title);
        }

        string[] valid = ["abc", "Bob.Smith", "j_doe-99", "mail@example.com", "a+b", "7of9", new string('z', 64)];
        foreach (var userName in valid)
        {
            await anonymous.PostJsonAsync("/api/auth/register/options", new { userName }).AssertStatusAsync(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Closed_registration_rejects_everyone()
    {
        await using var factory = new SyncedNotesFactory(new FactoryOptions { Registration = "Closed" });
        var anonymous = factory.CreateAnonymousClient();

        Assert.False(await RegistrationOpenAsync(anonymous));
        var (title, _) = await (await anonymous.PostJsonAsync("/api/auth/register/options", new { userName = "nobody" }))
            .ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Registration is closed.", title);
    }

    [Fact]
    public async Task Registration_options_follow_the_passkey_configuration_and_reject_bad_responses()
    {
        await using var factory = new SyncedNotesFactory(new FactoryOptions { Registration = "Open" });
        var browser = factory.CreateBrowserClient(); // keeps the ceremony state cookie

        var options = await (await browser.PostJsonAsync("/api/auth/register/options", new { userName = "carol" })).ReadJsonAsync();
        Assert.False(string.IsNullOrEmpty(options.Str("challenge")));
        Assert.Equal("localhost", options.GetProperty("rp").Str("id"));
        Assert.Equal("required", options.GetProperty("authenticatorSelection").Str("userVerification"));
        Assert.Equal("preferred", options.GetProperty("authenticatorSelection").Str("residentKey"));
        Assert.Equal(180_000, options.GetProperty("timeout").GetInt32());

        var (title, _) = await (await browser.PostJsonAsync("/api/auth/register/complete", new { credentialJson = """{"id":"bogus"}""" }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("The passkey couldn't be registered.", title);
        await (await browser.PostJsonAsync("/api/auth/register/complete", new { credentialJson = "" })).ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }
}

/// <summary>The anonymous passkey ceremony endpoints (the WebAuthn part itself needs a real authenticator).</summary>
public class PasskeyCeremonyTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private static HashSet<string> PropertyNames(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToHashSet();

    [Fact]
    public async Task Sign_in_options_for_an_unknown_username_look_like_a_username_less_sign_in()
    {
        var user = await factory.CreateUserAsync();
        var anonymous = factory.CreateAnonymousClient();

        var known = await (await anonymous.PostJsonAsync("/api/auth/login/options", new { userName = user.UserName })).ReadJsonAsync();
        var unknown = await (await anonymous.PostJsonAsync("/api/auth/login/options", new { userName = "no-such-user" })).ReadJsonAsync();
        var none = await (await anonymous.PostJsonAsync("/api/auth/login/options", new { })).ReadJsonAsync();

        foreach (var options in new[] { known, unknown, none })
        {
            Assert.False(string.IsNullOrEmpty(options.Str("challenge")));
            Assert.Equal("localhost", options.Str("rpId"));
            Assert.Equal("required", options.Str("userVerification"));
        }
        // An unknown name is indistinguishable from "pick any passkey" (discoverable credentials).
        Assert.Equal(PropertyNames(none), PropertyNames(unknown));
        if (unknown.TryGetProperty("allowCredentials", out var allow)) Assert.Empty(allow.EnumerateArray());
        Assert.NotEqual(unknown.Str("challenge"), none.Str("challenge"));
    }

    [Fact]
    public async Task An_invalid_passkey_assertion_does_not_sign_in()
    {
        var browser = factory.CreateBrowserClient(out var cookies);
        await browser.PostJsonAsync("/api/auth/login/options", new { }).AssertStatusAsync(HttpStatusCode.OK);

        var (title, _) = await (await browser.PostJsonAsync("/api/auth/login/complete",
            new { credentialJson = """{"id":"bogus","type":"public-key"}""", returnUrl = "/" })).ReadProblemAsync(HttpStatusCode.Unauthorized);

        Assert.Equal("That passkey wasn't recognised.", title);
        Assert.Null(SyncedNotesFactory.AuthCookie(cookies));
        await browser.GetAsync("/api/session").AssertStatusAsync(HttpStatusCode.Unauthorized);
        var (emptyTitle, _) = await (await browser.PostJsonAsync("/api/auth/login/complete", new { credentialJson = "" }))
            .ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("The browser didn't provide a passkey.", emptyTitle);
    }

    [Fact]
    public async Task An_invalid_passkey_cannot_unlock_the_app()
    {
        var user = await factory.CreateUserAsync();
        var locked = factory.CreateClientFor(user, locked: true);
        // The test client must keep the ceremony state cookie between the two calls.
        var browser = factory.CreateBrowserClient();
        browser.DefaultRequestHeaders.Add(TestAuth.UserHeader, user.Id);
        browser.DefaultRequestHeaders.Add(TestAuth.LockedHeader, "1");

        await browser.PostJsonAsync("/api/unlock/options", null).AssertStatusAsync(HttpStatusCode.OK);
        var (title, _) = await (await browser.PostJsonAsync("/api/unlock/passkey", new { credentialJson = """{"id":"bogus"}""" }))
            .ReadProblemAsync(HttpStatusCode.Unauthorized);

        Assert.Equal("That passkey couldn't be verified.", title);
        await locked.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Locked);
    }

    [Theory]
    [InlineData("/api/auth/login/complete", false)]
    [InlineData("/api/auth/register/complete", false)]
    [InlineData("/api/unlock/passkey", true)]
    [InlineData("/api/account/passkeys", true)]
    public async Task Finishing_a_passkey_ceremony_that_was_never_started_is_a_client_error(string path, bool signedIn)
    {
        // The ceremony state lives in a short-lived cookie (Identity.TwoFactorUserId, 5 minutes) that is consumed
        // by the first completion. It is missing when it expired, when another tab already finished its own
        // ceremony, or when a script calls the endpoint directly. That must not be a server error.
        var client = signedIn
            ? factory.CreateClientFor(await factory.CreateUserAsync())
            : factory.CreateAnonymousClient();

        var response = await client.PostJsonAsync(path, new { credentialJson = """{"id":"x","type":"public-key"}""" });

        Assert.True((int)response.StatusCode is >= 400 and < 500,
            $"POST {path} without a pending ceremony returned {(int)response.StatusCode} {response.StatusCode}.");
    }
}

/// <summary>One-time recovery codes: the service and POST /api/auth/recover.</summary>
public class RecoveryCodeTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private Task<IReadOnlyList<string>> GenerateAsync(string userId) =>
        factory.WithScopeAsync(sp => sp.GetRequiredService<RecoveryCodeService>().GenerateAsync(userId));

    private Task<bool> RedeemAsync(string userId, string code) =>
        factory.WithScopeAsync(sp => sp.GetRequiredService<RecoveryCodeService>().RedeemAsync(userId, code));

    private Task<int> RemainingAsync(string userId) =>
        factory.WithScopeAsync(sp => sp.GetRequiredService<RecoveryCodeService>().CountRemainingAsync(userId));

    private static Task<HttpResponseMessage> RecoverAsync(HttpClient client, string? userName, string? code) =>
        client.PostJsonAsync("/api/auth/recover", new { userName, code });

    [Fact]
    public async Task Ten_distinct_unambiguous_codes_are_generated_and_only_their_hashes_are_stored()
    {
        var user = await factory.CreateUserAsync();

        var codes = await GenerateAsync(user.Id);

        Assert.Equal(RecoveryCodeService.DefaultCount, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[23456789ABCDEFGHJKMNPQRSTVWXYZ]{5}-[23456789ABCDEFGHJKMNPQRSTVWXYZ]{5}$", c));
        var rows = await factory.WithDbAsync(db => db.RecoveryCodes.AsNoTracking().Where(r => r.UserId == user.Id).ToListAsync());
        Assert.Equal(codes.Count, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Matches("^[0-9a-f]{64}$", r.CodeHash);
            Assert.Null(r.UsedAt);
        });
        Assert.DoesNotContain(rows, r => codes.Any(c => r.CodeHash.Contains(c, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(10, await RemainingAsync(user.Id));
    }

    [Fact]
    public async Task A_code_can_be_redeemed_only_once()
    {
        var user = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);

        Assert.True(await RedeemAsync(user.Id, codes[0]));
        Assert.False(await RedeemAsync(user.Id, codes[0]));

        Assert.Equal(9, await RemainingAsync(user.Id));
        Assert.True(await RedeemAsync(user.Id, codes[1]));
        Assert.Equal(8, await RemainingAsync(user.Id));
    }

    [Fact]
    public async Task Codes_are_case_dash_and_whitespace_insensitive()
    {
        var user = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);

        Assert.True(await RedeemAsync(user.Id, codes[0].ToLowerInvariant()));
        Assert.True(await RedeemAsync(user.Id, codes[1].Replace("-", "")));
        Assert.True(await RedeemAsync(user.Id, $"  {codes[2][..5]} {codes[2][6..]}  "));
        Assert.True(await RedeemAsync(user.Id, codes[3].Replace("-", " - ").ToLowerInvariant()));
        Assert.Equal(6, await RemainingAsync(user.Id));
    }

    [Fact]
    public async Task Wrong_malformed_or_foreign_codes_are_rejected()
    {
        var user = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);
        var otherCodes = await GenerateAsync(other.Id);

        Assert.False(await RedeemAsync(user.Id, "22222-22222"));
        Assert.False(await RedeemAsync(user.Id, ""));
        Assert.False(await RedeemAsync(user.Id, codes[0][..^1]));     // too short
        Assert.False(await RedeemAsync(user.Id, codes[0] + "X"));     // too long
        Assert.False(await RedeemAsync(user.Id, otherCodes[0]));      // another account's code

        Assert.Equal(10, await RemainingAsync(user.Id));
        Assert.Equal(10, await RemainingAsync(other.Id));
    }

    [Fact]
    public async Task Generating_new_codes_invalidates_the_old_ones()
    {
        var user = await factory.CreateUserAsync();
        var old = await GenerateAsync(user.Id);
        var fresh = await GenerateAsync(user.Id);

        Assert.False(await RedeemAsync(user.Id, old[0]));
        Assert.True(await RedeemAsync(user.Id, fresh[0]));
        Assert.Equal(9, await RemainingAsync(user.Id));
    }

    [Fact]
    public async Task Recover_signs_in_with_a_valid_code_and_rejects_everything_else()
    {
        var user = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);
        var browser = factory.CreateBrowserClient(out var cookies);

        var (wrongTitle, _) = await (await RecoverAsync(browser, user.UserName, "22222-22222")).ReadProblemAsync(HttpStatusCode.Unauthorized);
        var (unknownTitle, _) = await (await RecoverAsync(browser, "no-such-user", codes[0])).ReadProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Equal("That username and recovery code didn't match.", wrongTitle);
        Assert.Equal(wrongTitle, unknownTitle); // usernames can't be probed
        await (await RecoverAsync(browser, user.UserName, "  ")).ReadProblemAsync(HttpStatusCode.BadRequest);
        await (await RecoverAsync(browser, null, codes[0])).ReadProblemAsync(HttpStatusCode.BadRequest);
        Assert.Null(SyncedNotesFactory.AuthCookie(cookies));

        var ok = await (await RecoverAsync(browser, $" {user.UserName} ", codes[0].ToLowerInvariant())).ReadJsonAsync();
        Assert.Equal("/?recovered=1", ok.Str("redirect"));
        Assert.Equal(9, ok.GetProperty("remaining").GetInt32());
        var session = await browser.GetJsonAsync("/api/session");
        Assert.Equal(user.Id, session.Str("userId"));
        Assert.Equal("recovery", session.Str("authMethod"));
        Assert.False(session.GetProperty("locked").GetBoolean());

        // The code is used up.
        var again = factory.CreateBrowserClient();
        await (await RecoverAsync(again, user.UserName, codes[0])).ReadProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_temporarily()
    {
        var user = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);
        var client = factory.CreateAnonymousClient();

        for (var i = 0; i < 5; i++)
        {
            await (await RecoverAsync(client, user.UserName, "22222-22222")).ReadProblemAsync(HttpStatusCode.Unauthorized);
        }

        var (title, _) = await (await RecoverAsync(client, user.UserName, codes[0])).ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("This account is temporarily locked.", title);
        Assert.Equal(10, await RemainingAsync(user.Id)); // the valid code was not used up
    }

    [Fact]
    public async Task A_successful_recovery_resets_the_failed_attempt_count()
    {
        var user = await factory.CreateUserAsync();
        var codes = await GenerateAsync(user.Id);
        var client = factory.CreateAnonymousClient();

        for (var i = 0; i < 4; i++) await RecoverAsync(client, user.UserName, "22222-22222");
        await RecoverAsync(client, user.UserName, codes[0]).AssertStatusAsync(HttpStatusCode.OK);
        for (var i = 0; i < 4; i++) await RecoverAsync(client, user.UserName, "22222-22222");

        await RecoverAsync(client, user.UserName, codes[1]).AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Codes_can_be_regenerated_and_counted_from_the_account_settings()
    {
        var user = await factory.CreateUserAsync();
        var old = await GenerateAsync(user.Id);
        var session = factory.CreateClientFor(user);

        var regenerated = await (await session.PostJsonAsync("/api/account/recovery-codes", null)).ReadJsonAsync();
        var codes = regenerated.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, (await session.GetJsonAsync("/api/account/recovery-codes")).GetProperty("remaining").GetInt32());

        var anonymous = factory.CreateAnonymousClient();
        await RecoverAsync(anonymous, user.UserName, old[0]).AssertStatusAsync(HttpStatusCode.Unauthorized);
        await RecoverAsync(anonymous, user.UserName, codes[0]).AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(9, (await session.GetJsonAsync("/api/account/recovery-codes")).GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Sign_in_endpoints_are_rate_limited_per_client_ip()
    {
        var ip = SyncedNotesFactory.RandomIp();
        var client = factory.CreateAnonymousClient(remoteIp: ip);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 41; i++) statuses.Add((await client.GetAsync("/api/auth/status")).StatusCode);

        Assert.All(statuses.Take(40), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[40]);
        await client.PostJsonAsync("/api/auth/recover", new { userName = "x", code = "y" }).AssertStatusAsync(HttpStatusCode.TooManyRequests);
        await factory.CreateAnonymousClient(remoteIp: SyncedNotesFactory.RandomIp()).GetAsync("/api/auth/status").AssertStatusAsync(HttpStatusCode.OK);
    }
}

/// <summary>The Razor sign-in pages.</summary>
public class SignInPageTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private static string ReturnUrlOf(string html) =>
        WebUtility.HtmlDecode(Regex.Match(html, "data-return-url=\"([^\"]*)\"").Groups[1].Value);

    [Theory]
    [InlineData("/notes?x=1", "/notes?x=1")]
    [InlineData("/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("evil.example", "/")]
    public async Task The_sign_in_page_only_keeps_local_return_urls(string returnUrl, string expected)
    {
        await factory.CreateUserAsync(); // so the page renders instead of sending us to registration
        var anonymous = factory.CreateAnonymousClient();

        var page = await anonymous.GetAsync($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        await page.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal(expected, ReturnUrlOf(await page.Content.ReadAsStringAsync()));

        // Already signed in: redirected straight to the (safe) return URL.
        var user = await factory.CreateUserAsync();
        var signedIn = await factory.CreateClientFor(user).GetAsync($"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(expected, signedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_sign_in_page_rejects_return_urls_smuggling_a_host_past_a_tab_or_newline()
    {
        // Browsers strip tabs and newlines from URLs, so "/<TAB>/evil.example" navigates to //evil.example.
        // account.js passes the page's return URL to /api/auth/login/complete and then does
        // location.assign(result.redirect), which makes this an open redirect after signing in.
        await factory.CreateUserAsync();
        var anonymous = factory.CreateAnonymousClient();

        var page = await anonymous.GetAsync("/Account/Login?returnUrl=%2F%09%2Fevil.example");
        await page.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("/", ReturnUrlOf(await page.Content.ReadAsStringAsync()));

        // A signed-in visitor should simply be sent home, not get a server error.
        var signedIn = await factory.CreateClientFor(await factory.CreateUserAsync())
            .GetAsync("/Account/Login?returnUrl=%2F%0A%2Fevil.example");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/", signedIn.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/Account/Recover")]
    [InlineData("/Account/Register")]
    [InlineData("/Account/Logout")]
    [InlineData("/offline")]
    public async Task Anonymous_pages_render(string path)
    {
        var page = await factory.CreateAnonymousClient().GetAsync(path);

        await page.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Signed_in_visitors_skip_the_registration_page()
    {
        var page = await factory.CreateClientFor(await factory.CreateUserAsync()).GetAsync("/Account/Register");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/", page.Headers.Location!.OriginalString);
    }
}
