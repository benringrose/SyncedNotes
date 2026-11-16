using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Tests.Infrastructure;
using static SyncedNotes.Tests.Infrastructure.Api;

namespace SyncedNotes.Tests;

/// <summary>CSRF protection for the cookie-authenticated APIs (AntiCsrfHeaderFilter).</summary>
public class CsrfProtectionTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    public static TheoryData<string, string, bool> StateChangingEndpoints => new()
    {
        // method, path, signed in
        { "POST", "/api/sync", true },
        { "PUT", "/api/attachments/{id}", true },
        { "POST", "/api/lock", true },
        { "POST", "/api/unlock/pin", true },
        { "POST", "/api/unlock/options", true },
        { "POST", "/api/logout", true },
        { "PUT", "/api/account/profile", true },
        { "PUT", "/api/account/app-lock", true },
        { "PUT", "/api/account/app-lock/pin", true },
        { "DELETE", "/api/account/app-lock/pin", true },
        { "POST", "/api/account/tokens", true },
        { "DELETE", "/api/account/tokens/{id}", true },
        { "POST", "/api/account/recovery-codes", true },
        { "POST", "/api/account/passkeys/options", true },
        { "POST", "/api/account/delete", true },
        { "POST", "/api/auth/register/options", false },
        { "POST", "/api/auth/login/options", false },
        { "POST", "/api/auth/recover", false },
    };

    private static HttpRequestMessage Request(string method, string path, TestUser user, string? csrf, string? secFetchSite)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("{id}", Guid.NewGuid().ToString()));
        if (method != "DELETE")
        {
            request.Content = path.Contains("attachments")
                ? Bytes(Png())
                : JsonBody(new
                {
                    pin = "1234", name = "x", displayName = "x", confirmUserName = user.UserName,
                    userName = user.UserName, code = "AAAAA-BBBBB", enabled = true, timeoutMinutes = 5, lockOnLaunch = true,
                });
        }
        if (csrf is not null) request.Headers.Add("X-SyncedNotes", csrf);
        if (secFetchSite is not null) request.Headers.Add("Sec-Fetch-Site", secFetchSite);
        return request;
    }

    private async Task<(TestUser User, HttpClient Client)> ClientAsync(bool signedIn)
    {
        var user = await factory.CreateUserAsync();
        return (user, signedIn ? factory.CreateClientFor(user, csrfHeader: false) : factory.CreateAnonymousClient(csrfHeader: false));
    }

    [Theory]
    [MemberData(nameof(StateChangingEndpoints))]
    public async Task State_changing_requests_without_the_header_are_rejected(string method, string path, bool signedIn)
    {
        var (user, client) = await ClientAsync(signedIn);

        foreach (var value in new[] { null, "0", "true", "" })
        {
            var response = await client.SendAsync(Request(method, path, user, csrf: value, secFetchSite: "same-origin"));
            var (title, detail) = await response.ReadProblemAsync(HttpStatusCode.BadRequest);
            Assert.Equal("Missing request header.", title);
            Assert.Contains("X-SyncedNotes: 1", detail);
        }

        // Nothing happened, e.g. the account still exists and still has no tokens.
        Assert.Equal(1, await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Id == user.Id)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ApiTokens.CountAsync(t => t.UserId == user.Id)));
    }

    [Theory]
    [MemberData(nameof(StateChangingEndpoints))]
    public async Task Cross_site_requests_are_forbidden_even_with_the_header(string method, string path, bool signedIn)
    {
        var (user, client) = await ClientAsync(signedIn);

        var response = await client.SendAsync(Request(method, path, user, csrf: "1", secFetchSite: "cross-site"));

        var (title, _) = await response.ReadProblemAsync(HttpStatusCode.Forbidden);
        Assert.Equal("Cross-site requests are not allowed.", title);
        Assert.Equal(1, await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Id == user.Id)));
    }

    [Fact]
    public async Task Same_origin_and_same_site_requests_with_the_header_are_accepted()
    {
        var (user, client) = await ClientAsync(signedIn: true);

        foreach (var site in new[] { "same-origin", "same-site", "none", null })
        {
            var response = await client.SendAsync(Request("POST", "/api/account/tokens", user, csrf: "1", secFetchSite: site));
            await response.AssertStatusAsync(HttpStatusCode.OK);
        }
        Assert.Equal(4, await factory.WithDbAsync(db => db.ApiTokens.CountAsync(t => t.UserId == user.Id)));
    }

    [Theory]
    [InlineData("/api/sync")]
    [InlineData("/api/session")]
    [InlineData("/api/account/profile")]
    [InlineData("/api/account/tokens")]
    [InlineData("/api/export")]
    [InlineData("/api/auth/status")]
    public async Task Reads_do_not_need_the_header(string path)
    {
        var (_, client) = await ClientAsync(signedIn: true);

        await client.GetAsync(path).AssertStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_integration_api_uses_tokens_instead_of_the_header()
    {
        var user = await factory.CreateUserAsync();
        var created = await (await factory.CreateClientFor(user).PostJsonAsync("/api/account/tokens", new { name = "cli" })).ReadJsonAsync();
        var api = factory.CreateAnonymousClient(csrfHeader: false);
        api.DefaultRequestHeaders.Authorization = new("Bearer", created.Str("token"));

        await api.PostJsonAsync("/api/v1/quick/notes", new { title = "From a script" }).AssertStatusAsync(HttpStatusCode.Created);
    }
}

/// <summary>QuickAddLinkGuardMiddleware: /new and /share links from other sites must be confirmed.</summary>
public class QuickAddLinkGuardTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    private async Task<HttpResponseMessage> NavigateAsync(string url, string? secFetchSite, TestUser? user = null)
    {
        var client = user is null ? factory.CreateAnonymousClient() : factory.CreateClientFor(user);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (secFetchSite is not null) request.Headers.Add("Sec-Fetch-Site", secFetchSite);
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData(null)] // older browsers without Fetch Metadata
    public async Task Quick_add_links_from_elsewhere_are_redirected_to_ask_for_confirmation(string? secFetchSite)
    {
        var response = await NavigateAsync("/new?text=x", secFetchSite);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/new?text=x&confirm=1", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("none")]          // typed, bookmarked or opened by the OS (e.g. PowerToys)
    [InlineData("same-origin")]   // from inside the app
    [InlineData("Same-Origin")]
    public async Task Quick_add_links_the_user_opened_are_not_flagged(string secFetchSite)
    {
        var response = await NavigateAsync("/new?text=x", secFetchSite);

        // Not touched by the guard: the normal sign-in redirect for an anonymous visitor.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/Account/Login", location);
        Assert.Contains("ReturnUrl=%2Fnew%3Ftext%3Dx", location);
        Assert.DoesNotContain("confirm", location);
    }

    [Fact]
    public async Task The_confirm_flag_survives_the_sign_in_redirect()
    {
        var first = await NavigateAsync("/new?text=hello%20world&tags=a&tags=b&confirm=0", "cross-site");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var confirmUrl = first.Headers.Location!.OriginalString;
        Assert.Equal("/new?text=hello%20world&tags=a&tags=b&confirm=1", confirmUrl);

        // The flagged URL is left alone by the guard and kept in the sign-in return URL.
        var second = await NavigateAsync(confirmUrl, "cross-site");
        var location = second.Headers.Location!.ToString();
        Assert.Contains("/Account/Login", location);
        Assert.Contains(Uri.EscapeDataString(confirmUrl), location);
    }

    [Theory]
    [InlineData("/share?title=a&text=b", "/share?title=a&text=b&confirm=1")]
    [InlineData("/NEW?text=x", "/NEW?text=x&confirm=1")]
    [InlineData("/new", "/new?confirm=1")]
    [InlineData("/new?confirm=yes&text=y", "/new?text=y&confirm=1")]
    public async Task Share_target_and_other_spellings_are_guarded_too(string url, string expected)
    {
        var response = await NavigateAsync(url, "cross-site");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expected, response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/new?text=spam&CONFIRM=1")]
    [InlineData("/new?text=spam&Confirm=1")]
    [InlineData("/share?text=spam&cOnFiRm=1")]
    public async Task A_differently_cased_confirm_flag_does_not_bypass_the_guard(string url)
    {
        // ASP.NET Core matches query keys case-insensitively, so the guard treats "CONFIRM=1" as already
        // confirmed. The page reads the flag with URLSearchParams.get('confirm') (main.js createFromParams),
        // which is case-sensitive, so it never asks and the other site's note is added silently.
        var user = await factory.CreateUserAsync();

        var response = await NavigateAsync(url, "cross-site", user);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("text=spam&confirm=1", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/Account/Login?returnUrl=%2Fnew%3Ftext%3Dspam")]
    [InlineData("/Account/Login?returnUrl=%2Fshare%3Ftext%3Dspam")]
    public async Task A_cross_site_link_to_the_sign_in_page_cannot_smuggle_in_an_unconfirmed_quick_add(string url)
    {
        // A link straight to /new is redirected with confirm=1 before the sign-in redirect, so the flag survives
        // signing in. A link to the sign-in page itself skips that: after signing in, account.js navigates to the
        // page's return URL from its own script (location.assign), the browser reports that navigation as
        // Sec-Fetch-Site: same-origin, the guard lets it through and main.js adds the note without asking.
        await factory.CreateUserAsync(); // so the sign-in page renders

        var response = await NavigateAsync(url, "cross-site");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        var returnUrl = WebUtility.HtmlDecode(Regex.Match(html, "data-return-url=\"([^\"]*)\"").Groups[1].Value);
        var quickAdd = returnUrl.StartsWith("/new", StringComparison.OrdinalIgnoreCase) ||
                       returnUrl.StartsWith("/share", StringComparison.OrdinalIgnoreCase);
        Assert.False(quickAdd && !returnUrl.Contains("confirm=1"), $"return URL after sign-in: {returnUrl}");
    }

    [Fact]
    public async Task A_quick_add_link_the_user_opened_while_signed_out_still_works_after_signing_in()
    {
        // e.g. a PowerToys Command Palette link: /new (Sec-Fetch-Site: none) -> sign-in page -> back to /new.
        await factory.CreateUserAsync();

        var first = await NavigateAsync("/new?text=x", "none");
        var login = new Uri(new Uri("http://localhost"), first.Headers.Location!).PathAndQuery;
        var page = await NavigateAsync(login, "none");

        await page.AssertStatusAsync(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal("/new?text=x", WebUtility.HtmlDecode(Regex.Match(html, "data-return-url=\"([^\"]*)\"").Groups[1].Value));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/open-file")]
    [InlineData("/Account/Login")]
    public async Task Other_pages_are_not_guarded(string url)
    {
        await factory.CreateUserAsync();

        var response = await NavigateAsync(url, "cross-site");

        Assert.DoesNotContain("confirm=1", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task Signed_in_users_get_the_app_shell_for_links_they_opened()
    {
        var user = await factory.CreateUserAsync();

        var response = await NavigateAsync("/new?text=x", "none", user);

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString()); // set by the app shell page only

        // A cross-site link is still redirected for signed-in users.
        var flagged = await NavigateAsync("/share?text=x", "cross-site", user);
        Assert.Equal("/share?text=x&confirm=1", flagged.Headers.Location!.OriginalString);
    }
}

/// <summary>Pipeline-wide behaviour: security headers, the service worker, health and the app shell.</summary>
public class WebPipelineTests(SyncedNotesFactory factory) : IClassFixture<SyncedNotesFactory>
{
    [Theory]
    [InlineData("/offline")]
    [InlineData("/Account/Recover")]
    [InlineData("/Account/Register")]
    [InlineData("/healthz")]
    [InlineData("/api/auth/status")]
    [InlineData("/api/sync")] // 401
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var response = await factory.CreateAnonymousClient().GetAsync(path);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("form-action 'self'", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Opener-Policy")));
        var permissions = Assert.Single(response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("camera=()", permissions);
        Assert.Contains("publickey-credentials-get=(self)", permissions);
    }

    [Fact]
    public async Task Html_pages_carry_no_inline_scripts()
    {
        // The CSP allows script-src 'self' only, so inline scripts would be blocked anyway.
        foreach (var path in new[] { "/offline", "/Account/Recover", "/Account/Register" })
        {
            var page = await factory.CreateAnonymousClient().GetAsync(path);
            Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
            var html = await page.Content.ReadAsStringAsync();
            foreach (Match script in Regex.Matches(html, "<script(?<attrs>[^>]*)>(?<body>.*?)</script>", RegexOptions.Singleline))
            {
                Assert.Contains("src=", script.Groups["attrs"].Value);
                Assert.True(string.IsNullOrWhiteSpace(script.Groups["body"].Value), $"inline script on {path}");
            }
        }
    }

    [Fact]
    public async Task The_service_worker_is_served_with_its_scope_header_and_precache_list()
    {
        var response = await factory.CreateAnonymousClient().GetAsync("/sw.js");

        await response.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("/", Assert.Single(response.Headers.GetValues("Service-Worker-Allowed")));
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        var script = await response.Content.ReadAsStringAsync();
        Assert.Contains("SN_PRECACHE", script);
        Assert.Matches("const SN_VERSION = '[0-9a-f]{12}';", script);
        Assert.Contains("addEventListener('fetch'", script); // the embedded worker template follows the header

        var precacheJson = Regex.Match(script, @"const SN_PRECACHE = (\[.*?\]);\n").Groups[1].Value;
        var precache = JsonSerializer.Deserialize<string[]>(precacheJson)!;
        Assert.Contains("/offline", precache);
        Assert.Contains("/manifest.webmanifest", precache);
        Assert.All(precache, p => Assert.StartsWith("/", p));
        Assert.DoesNotContain(precache, p => p.StartsWith("/icons/screenshots/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(precache, p => p.EndsWith(".map") || p.EndsWith(".html"));
    }

    [Fact]
    public async Task The_health_endpoint_is_anonymous()
    {
        var health = await factory.CreateAnonymousClient().GetJsonAsync("/healthz");

        Assert.Equal("ok", health.Str("status"));
        Assert.Matches(@"^\d+\.\d+\.\d+", health.Str("version"));
    }

    [Fact]
    public async Task The_app_shell_requires_sign_in()
    {
        var anonymous = await factory.CreateAnonymousClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Account/Login?ReturnUrl=%2F", anonymous.Headers.Location!.ToString());

        var signedIn = await factory.CreateClientFor(await factory.CreateUserAsync()).GetAsync("/");
        await signedIn.AssertStatusAsync(HttpStatusCode.OK);
        Assert.Equal("text/html", signedIn.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", signedIn.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Api_requests_get_status_codes_instead_of_login_redirects()
    {
        var anonymous = factory.CreateAnonymousClient();

        foreach (var path in new[] { "/api/sync", "/api/session", "/api/account/profile", "/api/export", $"/attachments/{Guid.NewGuid()}" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }
}
