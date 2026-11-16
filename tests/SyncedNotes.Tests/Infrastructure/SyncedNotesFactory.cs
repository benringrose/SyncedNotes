using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SyncedNotes.Auth;
using SyncedNotes.Data;

namespace SyncedNotes.Tests.Infrastructure;

/// <summary>Configuration for one test host.</summary>
public sealed class FactoryOptions
{
    /// <summary>Value for <c>Auth:Registration</c> (FirstUserOnly, Open or Closed).</summary>
    public string Registration { get; init; } = "FirstUserOnly";

    /// <summary>When set, the app (including the Identity cookie handlers) runs on this fake clock.</summary>
    public FakeTimeProvider? Clock { get; init; }
}

/// <summary>
/// Hosts SyncedNotes in memory with its own SQLite database in a fresh temp folder. Requests carrying
/// <see cref="TestAuth.UserHeader"/> are authenticated by <see cref="TestAuthHandler"/>; all other requests use
/// the real Identity application cookie, so cookie flows (recovery sign-in, lock/unlock) can be tested end to end.
/// </summary>
public class SyncedNotesFactory : WebApplicationFactory<Program>
{
    private readonly FactoryOptions _options;
    private int _cleanedUp;

    public SyncedNotesFactory() : this(new FactoryOptions())
    {
    }

    // xUnit class fixtures may only have one public constructor; tests that need a special host use this one.
    internal SyncedNotesFactory(FactoryOptions options)
    {
        _options = options;
        // Path.GetTempPath() honours TMPDIR, so test runs can be pointed at a scratch folder.
        DataDirectory = Path.Combine(Path.GetTempPath(), "syncednotes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public FakeTimeProvider? Clock => _options.Clock;

    public string ConnectionString => $"Data Source={Path.Combine(DataDirectory, "syncednotes.db")}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // UseSetting values reach WebApplication.CreateBuilder as command-line arguments, so they are visible to
        // the configuration reads at the top of Program.cs (DataDirectory, DisableBackgroundCleanup).
        builder.UseSetting("DataDirectory", DataDirectory);
        builder.UseSetting("DisableBackgroundCleanup", "true");
        builder.UseSetting("Auth:Registration", _options.Registration);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuth.Scheme, _ => { })
                .AddPolicyScheme(TestAuth.PolicyScheme, "Test header or Identity cookie", o =>
                    o.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey(TestAuth.UserHeader)
                        ? TestAuth.Scheme
                        : IdentityConstants.ApplicationScheme);
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuth.PolicyScheme;
                o.DefaultAuthenticateScheme = TestAuth.PolicyScheme;
                o.DefaultChallengeScheme = TestAuth.PolicyScheme;
                o.DefaultForbidScheme = TestAuth.PolicyScheme;
            });

            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();

            if (_options.Clock is { } clock)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<SecurityStampValidatorOptions>(o => o.TimeProvider = clock);
                services.PostConfigureAll<CookieAuthenticationOptions>(o => o.TimeProvider = clock);
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> with a fresh service scope.</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> action) =>
        WithScopeAsync<bool>(async sp => { await action(sp); return true; });

    /// <summary>Runs <paramref name="action"/> against the app's database.</summary>
    public Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action) =>
        WithScopeAsync(sp => action(sp.GetRequiredService<AppDbContext>()));

    public Task WithDbAsync(Func<AppDbContext, Task> action) =>
        WithScopeAsync(sp => action(sp.GetRequiredService<AppDbContext>()));

    /// <summary>Creates a real account (notes have a foreign key to AspNetUsers).</summary>
    public Task<TestUser> CreateUserAsync(string? userName = null) => WithScopeAsync(async sp =>
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            UserName = userName ?? $"user_{Guid.NewGuid():N}"[..20],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var result = await users.CreateAsync(user);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return new TestUser(user.Id, user.UserName!);
    });

    /// <summary>An HTTP client authenticated as <paramref name="user"/> through the test scheme.</summary>
    public HttpClient CreateClientFor(TestUser user, bool locked = false, bool csrfHeader = true)
    {
        var client = CreateAnonymousClient(csrfHeader);
        client.DefaultRequestHeaders.Add(TestAuth.UserHeader, user.Id);
        if (locked)
        {
            client.DefaultRequestHeaders.Add(TestAuth.LockedHeader, "1");
        }
        return client;
    }

    /// <summary>
    /// An unauthenticated client (no automatic redirects). Every client gets its own fake remote IP so the
    /// per-IP rate limit of the sign-in endpoints never couples unrelated tests.
    /// </summary>
    public HttpClient CreateAnonymousClient(bool csrfHeader = true, bool handleCookies = false, string? remoteIp = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = handleCookies,
        });
        client.DefaultRequestHeaders.Add(TestAuth.RemoteIpHeader, remoteIp ?? RandomIp());
        if (csrfHeader)
        {
            client.DefaultRequestHeaders.Add("X-SyncedNotes", "1");
        }
        return client;
    }

    /// <summary>A client that keeps cookies, i.e. behaves like a browser using the real Identity cookie.</summary>
    public HttpClient CreateBrowserClient() => CreateBrowserClient(out _);

    public HttpClient CreateBrowserClient(out CookieContainer cookies)
    {
        cookies = new CookieContainer();
        var client = CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(cookies));
        client.DefaultRequestHeaders.Add(TestAuth.RemoteIpHeader, RandomIp());
        client.DefaultRequestHeaders.Add("X-SyncedNotes", "1");
        return client;
    }

    /// <summary>The value of the Identity application cookie a browser client currently holds.</summary>
    public static string? AuthCookie(CookieContainer cookies) =>
        cookies.GetCookies(new Uri("http://localhost"))["SyncedNotes.Auth"]?.Value;

    /// <summary>
    /// Signs <paramref name="user"/> in the way a person without their passkey would: with a recovery code
    /// (POST /api/auth/recover). The returned client then authenticates with the real Identity cookie.
    /// </summary>
    public async Task<(HttpClient Browser, CookieContainer Cookies)> SignInWithRecoveryCodeAsync(TestUser user)
    {
        var codes = await WithScopeAsync(sp => sp.GetRequiredService<RecoveryCodeService>().GenerateAsync(user.Id));
        var browser = CreateBrowserClient(out var cookies);
        var response = await browser.PostJsonAsync("/api/auth/recover", new { userName = user.UserName, code = codes[0] });
        var body = await response.ReadJsonAsync();
        Assert.Equal("/?recovered=1", body.Str("redirect"));
        Assert.NotNull(AuthCookie(cookies));
        return (browser, cookies);
    }

    public static string RandomIp() =>
        $"10.{Random.Shared.Next(0, 256)}.{Random.Shared.Next(0, 256)}.{Random.Shared.Next(1, 255)}";

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Cleanup();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;
        try
        {
            using (var connection = new SqliteConnection(ConnectionString))
            {
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the folder lives in the temp directory anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record TestUser(string Id, string UserName);

/// <summary>Lets tests choose the client IP address (<see cref="TestAuth.RemoteIpHeader"/>).</summary>
internal sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[TestAuth.RemoteIpHeader].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }
            return nextMiddleware(context);
        });
        next(app);
    };
}
