using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SyncedNotes.Endpoints;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>The app lock (privacy screen): the lock claim, PIN unlock and lock settings.</summary>
public class AppLockTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private const string PinUrl = "/api/account/app-lock/pin";

    private static JsonElement AppLock(JsonElement session) => session.GetProperty("appLock");

    private async Task<(TestUser User, HttpClient Unlocked, HttpClient Locked)> UserWithPinAsync(string pin = "2468")
    {
        var user = await factory.CreateUserAsync();
        var unlocked = factory.CreateClientFor(user);
        await unlocked.PutJsonAsync(PinUrl, new { pin }).AssertStatusAsync(HttpStatusCode.OK);
        return (user, unlocked, factory.CreateClientFor(user, locked: true));
    }

    public static TheoryData<string, string> DataEndpoints => new()
    {
        { "GET", "/api/sync" },
        { "POST", "/api/sync" },
        { "GET", "/api/export" },
        { "PUT", "/api/attachments/{attachment}" },
        { "GET", "/attachments/{attachment}" },
        { "GET", "/api/account/profile" },
        { "PUT", "/api/account/profile" },
        { "GET", "/api/account/passkeys" },
        { "GET", "/api/account/tokens" },
        { "POST", "/api/account/tokens" },
        { "GET", "/api/account/recovery-codes" },
        { "POST", "/api/account/recovery-codes" },
        { "PUT", "/api/account/app-lock" },
        { "PUT", "/api/account/app-lock/pin" },
        { "DELETE", "/api/account/app-lock/pin" },
        { "POST", "/api/account/delete" },
    };

    [Theory]
    [MemberData(nameof(DataEndpoints))]
    public async Task A_locked_session_cannot_reach_notes_or_account_data(string method, string path)
    {
        var user = await factory.CreateUserAsync();
        var attachment = Guid.NewGuid(); // exists, so a 404 can't hide a missing lock check
        await factory.CreateClientFor(user).UploadAsync(attachment, Png()).AssertStatusAsync(HttpStatusCode.OK);
        var locked = factory.CreateClientFor(user, locked: true);

        var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("{attachment}", attachment.ToString()));
        if (method is "POST" or "PUT")
        {
            request.Content = path.Contains("attachments")
                ? Bytes(Png())
                : JsonBody(new { pin = "1234", name = "x", displayName = "x", confirmUserName = user.UserName, timeoutMinutes = 5 });
        }
        var response = await locked.SendAsync(request);

        var (title, _) = await response.ReadProblemAsync(HttpStatusCode.Locked);
        Assert.Equal("The app is locked.", title);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Id == user.Id)));
    }

    [Fact]
    public async Task The_session_endpoint_works_while_locked_and_reports_the_lock()
    {
        var user = await factory.CreateUserAsync();
        var locked = factory.CreateClientFor(user, locked: true);

        var session = await locked.GetJsonAsync("/api/session");

        Assert.True(session.GetProperty("locked").GetBoolean());
        Assert.Equal(user.Id, session.Str("userId"));
        Assert.Equal(user.UserName, session.Str("userName"));
        Assert.Equal(user.UserName, session.Str("displayName"));
        Assert.Equal(TestAuth.AuthMethod, session.Str("authMethod"));
        Assert.Equal("localhost", session.Str("rpId"));
        Assert.Empty(session.GetProperty("passkeyIds").EnumerateArray());
        Assert.InRange(session.GetProperty("serverTime").GetInt64(), NowMs() - 60_000, NowMs() + 1000);
        Assert.False(string.IsNullOrEmpty(session.Str("version")));
        var appLock = AppLock(session);
        Assert.False(appLock.GetProperty("enabled").GetBoolean());
        Assert.Equal(5, appLock.GetProperty("timeoutMinutes").GetInt32());
        Assert.True(appLock.GetProperty("lockOnLaunch").GetBoolean());
        Assert.False(appLock.GetProperty("hasPin").GetBoolean());
        Assert.False(appLock.GetProperty("pinAvailable").GetBoolean());

        Assert.False((await factory.CreateClientFor(user).GetJsonAsync("/api/session")).GetProperty("locked").GetBoolean());

        // Locking again and signing out are allowed while locked.
        Assert.True((await (await locked.PostJsonAsync("/api/lock", null)).ReadJsonAsync()).GetProperty("locked").GetBoolean());
        var logout = await (await locked.PostJsonAsync("/api/logout", null)).ReadJsonAsync();
        Assert.StartsWith("/Account/Login?", logout.Str("redirect"));
        // The flag in the redirect makes the sign-in page say what happened.
        var page = await factory.CreateAnonymousClient().GetAsync(logout.Str("redirect"));
        Assert.Contains("You've been signed out.", WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Session_and_lock_endpoints_require_a_signed_in_user()
    {
        var anonymous = factory.CreateAnonymousClient();

        await anonymous.GetAsync("/api/session").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await anonymous.PostJsonAsync("/api/lock", null).AssertStatusAsync(HttpStatusCode.Unauthorized);
        await anonymous.PostJsonAsync("/api/unlock/pin", new { pin = "1234" }).AssertStatusAsync(HttpStatusCode.Unauthorized);

        // A session whose account no longer exists is treated as signed out.
        var ghost = factory.CreateClientFor(new TestUser(Guid.NewGuid().ToString(), "ghost"));
        await (await ghost.GetAsync("/api/session")).ReadProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("12a4")]
    [InlineData("12 34")]
    [InlineData("-1234")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Pins_must_be_4_to_12_digits(string? pin)
    {
        var user = await factory.CreateUserAsync();
        var client = factory.CreateClientFor(user);

        var (title, _) = await (await client.PutJsonAsync(PinUrl, new { pin })).ReadProblemAsync(HttpStatusCode.BadRequest);

        Assert.Equal("A PIN must be 4 to 12 digits.", title);
        Assert.False(AppLock(await client.GetJsonAsync("/api/session")).GetProperty("hasPin").GetBoolean());
    }

    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("123456789012", "123456789012")]
    [InlineData(" 4321 ", "4321")]
    public async Task Valid_pins_are_stored_hashed_and_unlock_a_locked_session(string pin, string unlockWith)
    {
        var user = await factory.CreateUserAsync();
        var client = factory.CreateClientFor(user);

        var settings = await (await client.PutJsonAsync(PinUrl, new { pin })).ReadJsonAsync();
        Assert.True(settings.GetProperty("hasPin").GetBoolean());
        Assert.True(settings.GetProperty("pinAvailable").GetBoolean());
        var hash = await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.AppLockPinHash).SingleAsync());
        Assert.False(string.IsNullOrEmpty(hash));
        Assert.DoesNotContain(unlockWith, hash);

        var locked = factory.CreateClientFor(user, locked: true);
        var unlocked = await (await locked.PostJsonAsync("/api/unlock/pin", new { pin = unlockWith })).ReadJsonAsync();
        Assert.False(unlocked.GetProperty("locked").GetBoolean());
        Assert.True(unlocked.GetProperty("appLock").GetProperty("pinAvailable").GetBoolean());
    }

    [Fact]
    public async Task Wrong_pins_count_down_and_pin_unlock_is_disabled_after_five_failures()
    {
        var (_, unlocked, locked) = await UserWithPinAsync("2468");

        for (var attempt = 1; attempt < SessionEndpoints.MaxPinFailures; attempt++)
        {
            var (title, detail) = await (await locked.PostJsonAsync("/api/unlock/pin", new { pin = "1111" }))
                .ReadProblemAsync(HttpStatusCode.Unauthorized);
            var remaining = SessionEndpoints.MaxPinFailures - attempt;
            Assert.Equal("Incorrect PIN.", title);
            Assert.Equal($"{remaining} attempt{(remaining == 1 ? "" : "s")} left.", detail);
        }
        var (lastTitle, _) = await (await locked.PostJsonAsync("/api/unlock/pin", new { pin = "1111" }))
            .ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Too many incorrect PINs.", lastTitle);

        // Even the right PIN no longer works.
        var (title2, _) = await (await locked.PostJsonAsync("/api/unlock/pin", new { pin = "2468" })).ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("PIN unlock isn't available.", title2);
        var appLock = AppLock(await locked.GetJsonAsync("/api/session"));
        Assert.True(appLock.GetProperty("hasPin").GetBoolean());
        Assert.False(appLock.GetProperty("pinAvailable").GetBoolean());

        // Setting a new PIN (from an unlocked session, e.g. after unlocking with the passkey) enables it again.
        var settings = await (await unlocked.PutJsonAsync(PinUrl, new { pin = "1357" })).ReadJsonAsync();
        Assert.True(settings.GetProperty("pinAvailable").GetBoolean());
        await locked.PostJsonAsync("/api/unlock/pin", new { pin = "1357" }).AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_correct_pin_resets_the_failure_count()
    {
        var (user, _, locked) = await UserWithPinAsync("2468");
        await locked.PostJsonAsync("/api/unlock/pin", new { pin = "0000" }).AssertStatusAsync(HttpStatusCode.Unauthorized);
        await locked.PostJsonAsync("/api/unlock/pin", new { pin = "" }).AssertStatusAsync(HttpStatusCode.Unauthorized);

        await locked.PostJsonAsync("/api/unlock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.OK);

        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.AppLockPinFailures).SingleAsync()));
        for (var i = 0; i < SessionEndpoints.MaxPinFailures - 1; i++)
        {
            await locked.PostJsonAsync("/api/unlock/pin", new { pin = "0000" }).AssertStatusAsync(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Wrong_pins_sent_concurrently_still_count_towards_the_limit()
    {
        var (_, _, locked) = await UserWithPinAsync("2468");

        // Five wrong guesses fired at once (a script, not a person at the lock screen).
        var guesses = await Task.WhenAll(Enumerable.Range(0, SessionEndpoints.MaxPinFailures)
            .Select(i => locked.PostJsonAsync("/api/unlock/pin", new { pin = $"000{i}" })));
        Assert.All(guesses, r => Assert.True(r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, r.StatusCode.ToString()));

        // Those were five failed attempts, so PIN unlock must now be disabled - even for the right PIN.
        Assert.False(AppLock(await locked.GetJsonAsync("/api/session")).GetProperty("pinAvailable").GetBoolean());
        await locked.PostJsonAsync("/api/unlock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Pin_unlock_is_unavailable_without_a_pin()
    {
        var (_, unlocked, locked) = await UserWithPinAsync("2468");

        var removed = await (await unlocked.DeleteAsync(PinUrl)).ReadJsonAsync();
        Assert.False(removed.GetProperty("hasPin").GetBoolean());
        Assert.False(removed.GetProperty("pinAvailable").GetBoolean());

        var (title, _) = await (await locked.PostJsonAsync("/api/unlock/pin", new { pin = "2468" })).ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("PIN unlock isn't available.", title);
    }

    [Fact]
    public async Task Pin_unlock_attempts_are_rate_limited_per_user()
    {
        var (_, _, locked) = await UserWithPinAsync("2468");
        var (_, _, otherLocked) = await UserWithPinAsync("2468");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 16; i++)
        {
            statuses.Add((await locked.PostJsonAsync("/api/unlock/pin", new { pin = "0000" })).StatusCode);
        }

        Assert.All(statuses.Take(15), s => Assert.NotEqual(HttpStatusCode.TooManyRequests, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[15]);
        await otherLocked.PostJsonAsync("/api/unlock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(240)]
    public async Task Lock_settings_accept_the_supported_delays(int timeoutMinutes)
    {
        var client = factory.CreateClientFor(await factory.CreateUserAsync());

        var settings = await (await client.PutJsonAsync("/api/account/app-lock",
            new { enabled = true, timeoutMinutes, lockOnLaunch = false })).ReadJsonAsync();

        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(timeoutMinutes, settings.GetProperty("timeoutMinutes").GetInt32());
        Assert.False(settings.GetProperty("lockOnLaunch").GetBoolean());
        var session = AppLock(await client.GetJsonAsync("/api/session"));
        Assert.True(session.GetProperty("enabled").GetBoolean());
        Assert.Equal(timeoutMinutes, session.GetProperty("timeoutMinutes").GetInt32());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-5)]
    [InlineData(100000)]
    public async Task Lock_settings_reject_other_delays(int timeoutMinutes)
    {
        var client = factory.CreateClientFor(await factory.CreateUserAsync());

        var (title, _) = await (await client.PutJsonAsync("/api/account/app-lock",
            new { enabled = true, timeoutMinutes, lockOnLaunch = true })).ReadProblemAsync(HttpStatusCode.BadRequest);

        Assert.Equal("Unsupported auto-lock delay.", title);
        Assert.False(AppLock(await client.GetJsonAsync("/api/session")).GetProperty("enabled").GetBoolean());
    }
}

/// <summary>The same flows with the real Identity application cookie instead of the test scheme.</summary>
public class CookieSessionTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    [Fact]
    public async Task Lock_and_pin_unlock_round_trip_with_the_real_auth_cookie()
    {
        var user = await factory.CreateUserAsync();
        var (browser, cookies) = await factory.SignInWithRecoveryCodeAsync(user);

        var session = await browser.GetJsonAsync("/api/session");
        Assert.Equal(user.Id, session.Str("userId"));
        Assert.False(session.GetProperty("locked").GetBoolean());
        Assert.Equal("recovery", session.Str("authMethod"));
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.OK);
        await browser.PutJsonAsync("/api/account/app-lock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.OK);

        // Locking re-issues the (encrypted) auth cookie with the lock claim, so the server enforces it.
        var unlockedCookie = SyncedNotesFactory.AuthCookie(cookies);
        Assert.True((await (await browser.PostJsonAsync("/api/lock", null)).ReadJsonAsync()).GetProperty("locked").GetBoolean());
        Assert.NotEqual(unlockedCookie, SyncedNotesFactory.AuthCookie(cookies));
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Locked);
        await browser.GetAsync("/api/account/profile").AssertStatusAsync(HttpStatusCode.Locked);
        var lockedSession = await browser.GetJsonAsync("/api/session");
        Assert.True(lockedSession.GetProperty("locked").GetBoolean());
        Assert.Equal("recovery", lockedSession.Str("authMethod"));

        // A wrong PIN keeps the session locked.
        await browser.PostJsonAsync("/api/unlock/pin", new { pin = "1111" }).AssertStatusAsync(HttpStatusCode.Unauthorized);
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Locked);

        var unlocked = await (await browser.PostJsonAsync("/api/unlock/pin", new { pin = "2468" })).ReadJsonAsync();
        Assert.False(unlocked.GetProperty("locked").GetBoolean());
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("recovery", (await browser.GetJsonAsync("/api/session")).Str("authMethod"));

        // Signing out ends the session.
        await browser.PostJsonAsync("/api/logout", null).AssertStatusAsync(HttpStatusCode.OK);
        await browser.GetAsync("/api/session").AssertStatusAsync(HttpStatusCode.Unauthorized);
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_auth_cookie_is_http_only_and_same_site_lax()
    {
        var user = await factory.CreateUserAsync();
        var codes = await factory.WithScopeAsync(sp => sp.GetRequiredService<SyncedNotes.Auth.RecoveryCodeService>().GenerateAsync(user.Id));
        var client = factory.CreateAnonymousClient();

        var response = await client.PostJsonAsync("/api/auth/recover", new { userName = user.UserName, code = codes[0] });

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("SyncedNotes.Auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase); // persistent: an installed PWA stays signed in
    }

    [Fact]
    public async Task The_lock_survives_the_periodic_security_stamp_refresh()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var clockFactory = new SyncedNotesFactory(new FactoryOptions { Clock = clock });
        var user = await clockFactory.CreateUserAsync();
        var (browser, cookies) = await clockFactory.SignInWithRecoveryCodeAsync(user);
        await browser.PutJsonAsync("/api/account/app-lock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.OK);
        await browser.PostJsonAsync("/api/lock", null).AssertStatusAsync(HttpStatusCode.OK);
        var lockedCookie = SyncedNotesFactory.AuthCookie(cookies);

        // Identity re-validates the cookie every 30 minutes and rebuilds the principal from the database.
        clock.Advance(TimeSpan.FromMinutes(31));
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Locked);

        Assert.NotEqual(lockedCookie, SyncedNotesFactory.AuthCookie(cookies)); // the cookie really was refreshed...
        var session = await browser.GetJsonAsync("/api/session");
        Assert.True(session.GetProperty("locked").GetBoolean());             // ...and kept the lock
        Assert.Equal("recovery", session.Str("authMethod"));

        // Unlocked sessions stay unlocked across a refresh.
        await browser.PostJsonAsync("/api/unlock/pin", new { pin = "2468" }).AssertStatusAsync(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMinutes(31));
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_security_stamp_signs_out_other_sessions_at_the_next_validation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var clockFactory = new SyncedNotesFactory(new FactoryOptions { Clock = clock });
        var user = await clockFactory.CreateUserAsync();
        var (browser, _) = await clockFactory.SignInWithRecoveryCodeAsync(user);
        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.OK);

        await clockFactory.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<SyncedNotes.Data.AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByIdAsync(user.Id))!);
        });
        clock.Advance(TimeSpan.FromMinutes(31));

        await browser.GetAsync("/api/sync").AssertStatusAsync(HttpStatusCode.Unauthorized);
    }
}
