using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Endpoints;
using SyncedNotes.Infrastructure;
using SyncedNotes.Services;
using SyncedNotes.Sync;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------------------------------------------------------------------------------------------
// Database (SQLite). Everything - notes, images, accounts and data protection keys - lives in one file.
// ---------------------------------------------------------------------------------------------
var dataDirectory = config["DataDirectory"] is { Length: > 0 } dir
    ? Path.GetFullPath(dir, builder.Environment.ContentRootPath)
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);
var connectionString = config.GetConnectionString("Default") is { Length: > 0 } cs
    ? cs
    : $"Data Source={Path.Combine(dataDirectory, "syncednotes.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// Keys are stored in the database so sign-in cookies survive restarts and container rebuilds.
builder.Services.AddDataProtection()
    .SetApplicationName("SyncedNotes")
    .PersistKeysToDbContext<AppDbContext>();

// ---------------------------------------------------------------------------------------------
// Authentication: ASP.NET Core Identity with passkeys (WebAuthn) + API tokens for integrations.
// ---------------------------------------------------------------------------------------------
builder.Services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
var authOptions = config.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenDefaults.Scheme, null)
    .AddIdentityCookies();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // includes the passkeys table
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<IdentityPasskeyOptions>(options =>
{
    // Defaults to the request host. Set Passkeys:ServerDomain when the app is reachable on several host names.
    options.ServerDomain = config["Passkeys:ServerDomain"] is { Length: > 0 } domain ? domain : null;
    options.UserVerificationRequirement = "required";
    options.ResidentKeyRequirement = "preferred";
    options.AuthenticatorTimeout = TimeSpan.FromMinutes(3);
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "SyncedNotes.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(Math.Clamp(authOptions.SessionDays, 1, 400));
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/Login";
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.IsApiRequest())
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.IsApiRequest())
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.FromMinutes(30);
    // When Identity periodically re-validates the cookie it rebuilds the principal; keep the app-lock state.
    options.OnRefreshingPrincipal = context =>
    {
        if (context.NewPrincipal?.Identities.FirstOrDefault() is { } identity && context.CurrentPrincipal is { } current)
        {
            foreach (var type in new[] { AppClaims.Locked, AppClaims.AuthMethod })
            {
                if (current.FindFirst(type) is { } claim && !identity.HasClaim(c => c.Type == type))
                {
                    identity.AddClaim(new Claim(claim.Type, claim.Value));
                }
            }
        }
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ApiTokenDefaults.Policy, policy => policy
        .AddAuthenticationSchemes(ApiTokenDefaults.Scheme)
        .RequireAuthenticatedUser());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    options.AddPolicy(RateLimits.Auth, context => RateLimitPartition.GetFixedWindowLimiter(Ip(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 40, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(RateLimits.Unlock, context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Ip(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy(RateLimits.Quick, context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ApiTokenDefaults.TokenIdClaim) ?? Ip(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

// ---------------------------------------------------------------------------------------------
// App services
// ---------------------------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizePage("/Index");
    // Deep links handled by the client-side app (quick add, Web Share Target, file handler).
    options.Conventions.AddPageRoute("/Index", "new");
    options.Conventions.AddPageRoute("/Index", "share");
    options.Conventions.AddPageRoute("/Index", "open-file");
});
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<AppSignInService>();
builder.Services.AddScoped<RecoveryCodeService>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<RegistrationPolicy>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddSingleton<ServiceWorkerScript>();
if (!config.GetValue<bool>("DisableBackgroundCleanup"))
{
    builder.Services.AddHostedService<CleanupService>();
}

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Database migrations (applied automatically) and command-line tools.
// ---------------------------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    if (db.Database.IsSqlite())
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}

if (args is ["recovery-codes", var targetUser, ..])
{
    // Lost every passkey and recovery code? Run:  dotnet SyncedNotes.dll recovery-codes <username>
    await using var scope = app.Services.CreateAsyncScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var user = await users.FindByNameAsync(targetUser);
    if (user is null)
    {
        Console.Error.WriteLine($"No account named '{targetUser}'.");
        Environment.ExitCode = 1;
        return;
    }
    var codes = await scope.ServiceProvider.GetRequiredService<RecoveryCodeService>().GenerateAsync(user.Id);
    Console.WriteLine($"New recovery codes for {user.UserName} (previous codes no longer work):");
    foreach (var code in codes) Console.WriteLine($"  {code}");
    Console.WriteLine("Sign in at /Account/Recover with one of these codes, then add a new passkey.");
    return;
}

// ---------------------------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseHttpsRedirection();
app.UseMiddleware<QuickAddLinkGuardMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.MapGet("/sw.js", (ServiceWorkerScript sw, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["Service-Worker-Allowed"] = "/";
    return Results.Text(sw.Script, "text/javascript; charset=utf-8");
}).AllowAnonymous().ExcludeFromDescription();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = AppInfo.Version })).AllowAnonymous();

app.MapAuthEndpoints();
app.MapSessionEndpoints();
app.MapAccountEndpoints();
app.MapNoteEndpoints();
app.MapQuickEndpoints();

app.Run();

/// <summary>Exposed for integration tests (WebApplicationFactory).</summary>
public partial class Program;
