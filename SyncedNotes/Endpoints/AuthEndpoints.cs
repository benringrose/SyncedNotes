using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;
using SyncedNotes.Services;

namespace SyncedNotes.Endpoints;

public sealed record RegisterOptionsRequest(string? UserName, string? DisplayName);
public sealed record RegisterCompleteRequest(string? CredentialJson, string? PasskeyName);
public sealed record LoginOptionsRequest(string? UserName);
public sealed record LoginCompleteRequest(string? CredentialJson, bool? RememberMe, string? ReturnUrl);
public sealed record RecoverRequest(string? UserName, string? Code, string? ReturnUrl);

/// <summary>Anonymous endpoints used by the sign-in and registration pages.</summary>
public static partial class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .AllowAnonymous()
            .AddEndpointFilter<AntiCsrfHeaderFilter>()
            .RequireRateLimiting(RateLimits.Auth)
            .WithTags("Auth");

        group.MapGet("/status", async (RegistrationPolicy registration, CancellationToken ct) =>
            Results.Ok(new { registrationOpen = await registration.IsOpenAsync(ct) }));

        group.MapPost("/register/options", RegisterOptionsAsync);
        group.MapPost("/register/complete", RegisterCompleteAsync);
        group.MapPost("/login/options", LoginOptionsAsync);
        group.MapPost("/login/complete", LoginCompleteAsync);
        group.MapPost("/recover", RecoverAsync);
    }

    private static async Task<IResult> RegisterOptionsAsync(
        RegisterOptionsRequest request,
        RegistrationPolicy registration,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        CancellationToken ct)
    {
        if (!await registration.IsOpenAsync(ct))
        {
            return ApiProblems.Forbidden("Registration is closed.", "This SyncedNotes server isn't accepting new accounts.");
        }

        var userName = request.UserName?.Trim() ?? "";
        if (!UserNameRegex().IsMatch(userName))
        {
            return ApiProblems.BadRequest("Choose a different username.",
                "Use 3–64 letters, numbers or . _ - @ + characters, starting with a letter or number.");
        }
        if (await users.FindByNameAsync(userName) is not null)
        {
            return ApiProblems.Conflict("That username is already taken.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? userName : request.DisplayName.Trim();
        if (displayName.Length > 64) displayName = displayName[..64];

        var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = userName,
            DisplayName = displayName,
        });
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> RegisterCompleteAsync(
        RegisterCompleteRequest request,
        RegistrationPolicy registration,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AppSignInService appSignIn,
        RecoveryCodeService recoveryCodes,
        SeedService seed,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.CredentialJson))
        {
            return ApiProblems.BadRequest("The browser didn't provide a passkey.");
        }

        var attestation = await appSignIn.AttestAsync(request.CredentialJson);
        if (attestation is null)
        {
            return CeremonyExpired();
        }
        if (!attestation.Succeeded)
        {
            return ApiProblems.BadRequest("The passkey couldn't be registered.", attestation.Failure?.Message);
        }

        await RegistrationPolicy.CreationLock.WaitAsync(ct);
        AppUser user;
        try
        {
            if (!await registration.IsOpenAsync(ct))
            {
                return ApiProblems.Forbidden("Registration is closed.");
            }

            var entity = attestation.UserEntity;
            user = new AppUser
            {
                Id = entity.Id,
                UserName = entity.Name,
                DisplayName = entity.DisplayName,
                CreatedAt = time.NowMs(),
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return ApiProblems.BadRequest("The account couldn't be created.",
                    string.Join(" ", created.Errors.Select(e => e.Description)));
            }
        }
        finally
        {
            RegistrationPolicy.CreationLock.Release();
        }

        var passkey = attestation.Passkey;
        passkey.Name = CleanPasskeyName(request.PasskeyName);
        var added = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!added.Succeeded)
        {
            await users.DeleteAsync(user);
            return ApiProblems.BadRequest("The passkey couldn't be saved.");
        }

        await seed.SeedNewUserAsync(user.Id, ct);
        var codes = await recoveryCodes.GenerateAsync(user.Id, ct: ct);
        await appSignIn.SignInAsync(user, persistent: true, method: "pk", locked: false);

        loggerFactory.CreateLogger("SyncedNotes.Auth").LogInformation("Account {UserName} created.", user.UserName);
        return Results.Ok(new { recoveryCodes = codes, redirect = "/" });
    }

    private static async Task<IResult> LoginOptionsAsync(
        LoginOptionsRequest request,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        // With no username the browser offers any passkey it holds for this site (discoverable credentials).
        // An unknown username behaves exactly like no username so account names can't be probed.
        AppUser? user = null;
        if (!string.IsNullOrWhiteSpace(request.UserName))
        {
            user = await users.FindByNameAsync(request.UserName.Trim());
        }

        var optionsJson = await signIn.MakePasskeyRequestOptionsAsync(user);
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> LoginCompleteAsync(LoginCompleteRequest request, AppSignInService appSignIn)
    {
        if (string.IsNullOrEmpty(request.CredentialJson))
        {
            return ApiProblems.BadRequest("The browser didn't provide a passkey.");
        }

        var (result, _) = await appSignIn.PasskeySignInAsync(request.CredentialJson, request.RememberMe ?? true);
        if (result is null)
        {
            return CeremonyExpired();
        }
        if (result.Succeeded)
        {
            return Results.Ok(new { redirect = HttpRequestExtensions.SafeLocalUrl(request.ReturnUrl) });
        }
        if (result.IsLockedOut)
        {
            return ApiProblems.Forbidden("This account is temporarily locked.");
        }
        if (result.IsNotAllowed)
        {
            return ApiProblems.Forbidden("This account can't sign in.");
        }
        return ApiProblems.Unauthorized("That passkey wasn't recognised.",
            "It may belong to a different account or have been removed. Try another passkey or use a recovery code.");
    }

    private static async Task<IResult> RecoverAsync(
        RecoverRequest request,
        UserManager<AppUser> users,
        RecoveryCodeService recoveryCodes,
        AppSignInService appSignIn,
        CancellationToken ct)
    {
        const string failure = "That username and recovery code didn't match.";
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Code))
        {
            return ApiProblems.BadRequest("Enter your username and a recovery code.");
        }

        var user = await users.FindByNameAsync(request.UserName.Trim());
        if (user is null)
        {
            return ApiProblems.Unauthorized(failure);
        }
        if (users.SupportsUserLockout && await users.IsLockedOutAsync(user))
        {
            return ApiProblems.Forbidden("This account is temporarily locked.");
        }

        if (!await recoveryCodes.RedeemAsync(user.Id, request.Code, ct))
        {
            await users.AccessFailedAsync(user);
            return ApiProblems.Unauthorized(failure);
        }

        await users.ResetAccessFailedCountAsync(user);
        await appSignIn.SignInAsync(user, persistent: true, method: "recovery", locked: false);
        var remaining = await recoveryCodes.CountRemainingAsync(user.Id, ct);
        return Results.Ok(new { redirect = "/?recovered=1", remaining });
    }

    /// <summary>The browser finished a passkey ceremony the server has no record of (never started, or expired).</summary>
    public static IResult CeremonyExpired() =>
        ApiProblems.BadRequest("The passkey request expired.", "Please try again.");

    public static string CleanPasskeyName(string? name)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? "Passkey" : name.Trim();
        clean = new string(clean.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > 60 ? clean[..60] : clean;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._@+\-]{2,63}$")]
    private static partial Regex UserNameRegex();
}

public static class RateLimits
{
    public const string Auth = "auth";
    public const string Unlock = "unlock";
    public const string Quick = "quick";
}
