using System.Buffers.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Services;

namespace SyncedNotes.Endpoints;

public sealed record UnlockPasskeyRequest(string? CredentialJson);
public sealed record UnlockPinRequest(string? Pin);

/// <summary>Session information plus app lock / unlock. These endpoints work while the app is locked.</summary>
public static class SessionEndpoints
{
    public const int MaxPinFailures = 5;

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
            .RequireAuthorization()
            .AddEndpointFilter<AntiCsrfHeaderFilter>()
            .WithTags("Session");

        group.MapGet("/session", GetSessionAsync);
        group.MapPost("/lock", LockAsync);
        group.MapPost("/unlock/options", UnlockOptionsAsync).RequireRateLimiting(RateLimits.Unlock);
        group.MapPost("/unlock/passkey", UnlockWithPasskeyAsync).RequireRateLimiting(RateLimits.Unlock);
        group.MapPost("/unlock/pin", UnlockWithPinAsync).RequireRateLimiting(RateLimits.Unlock);
        group.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Ok(new { redirect = "/Account/Login?signedOut=true" });
        });
    }

    public static object LockSettings(AppUser user) => new
    {
        enabled = user.AppLockEnabled,
        timeoutMinutes = user.AppLockTimeoutMinutes,
        lockOnLaunch = user.AppLockOnLaunch,
        hasPin = user.AppLockPinHash is not null,
        pinAvailable = user.AppLockPinHash is not null && user.AppLockPinFailures < MaxPinFailures,
    };

    private static async Task<IResult> GetSessionAsync(
        ClaimsPrincipal principal,
        HttpRequest request,
        UserManager<AppUser> users,
        IOptions<IdentityPasskeyOptions> passkeyOptions,
        TimeProvider time)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return ApiProblems.Unauthorized("Not signed in.");
        }

        var passkeys = await users.GetPasskeysAsync(user);
        return Results.Ok(new
        {
            userId = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName ?? user.UserName,
            locked = principal.IsAppLocked(),
            authMethod = principal.FindFirstValue(AppClaims.AuthMethod),
            appLock = LockSettings(user),
            // Lets the client verify a passkey locally when unlocking offline.
            passkeyIds = passkeys.Select(p => Base64Url.EncodeToString(p.CredentialId)).ToArray(),
            rpId = passkeyOptions.Value.ServerDomain ?? request.Host.Host,
            serverTime = time.NowMs(),
            version = AppInfo.Version,
        });
    }

    private static async Task<IResult> LockAsync(ClaimsPrincipal principal, UserManager<AppUser> users, AppSignInService signIn)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (!principal.IsAppLocked())
        {
            await signIn.SetLockedAsync(user, locked: true);
        }
        return Results.Ok(new { locked = true });
    }

    private static async Task<IResult> UnlockOptionsAsync(
        ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        var optionsJson = await signIn.MakePasskeyRequestOptionsAsync(user);
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> UnlockWithPasskeyAsync(
        UnlockPasskeyRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        AppSignInService appSignIn,
        AppDbContext db)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (string.IsNullOrEmpty(request.CredentialJson)) return ApiProblems.BadRequest("The browser didn't provide a passkey.");

        var assertion = await appSignIn.AssertAsync(request.CredentialJson);
        if (assertion is null) return AuthEndpoints.CeremonyExpired();
        if (!assertion.Succeeded || assertion.User.Id != user.Id)
        {
            return ApiProblems.Unauthorized("That passkey couldn't be verified.");
        }

        await users.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
        await ResetPinFailuresAsync(db, user);
        await appSignIn.SetLockedAsync(user, locked: false);
        return Results.Ok(new { locked = false, appLock = LockSettings(user) });
    }

    private static async Task<IResult> UnlockWithPinAsync(
        UnlockPinRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        IPasswordHasher<AppUser> hasher,
        AppSignInService appSignIn,
        AppDbContext db)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (user.AppLockPinHash is null)
        {
            return ApiProblems.Forbidden("PIN unlock isn't available.", "Unlock with your passkey instead.");
        }

        // Reserve an attempt atomically before checking the PIN, so guesses sent in parallel all count towards
        // the limit (a read-increment-write here could let concurrent requests reuse the same attempt).
        var reserved = await db.Users
            .Where(u => u.Id == user.Id && u.AppLockPinFailures < MaxPinFailures)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AppLockPinFailures, u => u.AppLockPinFailures + 1));
        if (reserved == 0)
        {
            return ApiProblems.Forbidden("PIN unlock isn't available.", "Too many incorrect PINs. Unlock with your passkey instead.");
        }

        var pin = request.Pin?.Trim() ?? "";
        var verified = pin.Length > 0 &&
            hasher.VerifyHashedPassword(user, user.AppLockPinHash, pin) != PasswordVerificationResult.Failed;
        if (!verified)
        {
            var failures = await db.Users.Where(u => u.Id == user.Id).Select(u => u.AppLockPinFailures).FirstAsync();
            var remaining = Math.Max(0, MaxPinFailures - failures);
            return remaining == 0
                ? ApiProblems.Forbidden("Too many incorrect PINs.", "PIN unlock is disabled until you unlock with your passkey.")
                : ApiProblems.Unauthorized("Incorrect PIN.", $"{remaining} attempt{(remaining == 1 ? "" : "s")} left.");
        }

        await ResetPinFailuresAsync(db, user);
        await appSignIn.SetLockedAsync(user, locked: false);
        return Results.Ok(new { locked = false, appLock = LockSettings(user) });
    }

    private static async Task ResetPinFailuresAsync(AppDbContext db, AppUser user)
    {
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AppLockPinFailures, 0));
        // Keep the tracked entity in step without marking it modified.
        user.AppLockPinFailures = 0;
        db.Entry(user).Property(u => u.AppLockPinFailures).IsModified = false;
    }
}
