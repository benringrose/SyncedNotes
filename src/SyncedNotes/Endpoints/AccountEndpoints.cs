using System.Buffers.Text;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Auth;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;

namespace SyncedNotes.Endpoints;

public sealed record ProfileRequest(string? DisplayName);
public sealed record AddPasskeyRequest(string? CredentialJson, string? Name);
public sealed record RenamePasskeyRequest(string? Name);
public sealed record AppLockSettingsRequest(bool Enabled, int TimeoutMinutes, bool LockOnLaunch);
public sealed record PinRequest(string? Pin);
public sealed record CreateTokenRequest(string? Name);
public sealed record DeleteAccountRequest(string? ConfirmUserName);

/// <summary>Account settings: profile, passkeys, recovery codes, app lock and API tokens.</summary>
public static partial class AccountEndpoints
{
    private static readonly int[] AllowedTimeouts = [-1, 0, 1, 2, 5, 10, 15, 30, 60, 240];

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account")
            .RequireAuthorization()
            .AddEndpointFilter<AntiCsrfHeaderFilter>()
            .AddEndpointFilter<RequireUnlockedFilter>()
            .WithTags("Account");

        group.MapGet("/profile", async (ClaimsPrincipal principal, UserManager<AppUser> users) =>
            await users.GetUserAsync(principal) is { } user
                ? Results.Ok(new { user.UserName, displayName = user.DisplayName ?? user.UserName, user.CreatedAt })
                : ApiProblems.Unauthorized("Not signed in."));

        group.MapPut("/profile", UpdateProfileAsync);

        group.MapGet("/passkeys", ListPasskeysAsync);
        group.MapPost("/passkeys/options", PasskeyCreationOptionsAsync);
        group.MapPost("/passkeys", AddPasskeyAsync);
        group.MapPut("/passkeys/{id}", RenamePasskeyAsync);
        group.MapDelete("/passkeys/{id}", DeletePasskeyAsync);

        group.MapGet("/recovery-codes", async (ClaimsPrincipal principal, RecoveryCodeService codes, CancellationToken ct) =>
            Results.Ok(new { remaining = await codes.CountRemainingAsync(principal.GetUserId(), ct) }));
        group.MapPost("/recovery-codes", async (ClaimsPrincipal principal, RecoveryCodeService codes, CancellationToken ct) =>
            Results.Ok(new { codes = await codes.GenerateAsync(principal.GetUserId(), ct: ct) }));

        group.MapPut("/app-lock", UpdateAppLockAsync);
        group.MapPut("/app-lock/pin", SetPinAsync);
        group.MapDelete("/app-lock/pin", RemovePinAsync);

        group.MapGet("/tokens", async (ClaimsPrincipal principal, ApiTokenService tokens, CancellationToken ct) =>
            Results.Ok((await tokens.ListAsync(principal.GetUserId(), ct)).Select(t => new
            {
                t.Id,
                t.Name,
                t.Prefix,
                t.CreatedAt,
                t.LastUsedAt,
            })));
        group.MapPost("/tokens", CreateTokenAsync);
        group.MapDelete("/tokens/{id:guid}", async (Guid id, ClaimsPrincipal principal, ApiTokenService tokens, CancellationToken ct) =>
            await tokens.RevokeAsync(principal.GetUserId(), id, ct) ? Results.NoContent() : ApiProblems.NotFound());

        group.MapPost("/delete", DeleteAccountAsync);
    }

    private static async Task<IResult> UpdateProfileAsync(ProfileRequest request, ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        var name = request.DisplayName?.Trim();
        user.DisplayName = string.IsNullOrEmpty(name) ? null : name.Length > 64 ? name[..64] : name;
        await users.UpdateAsync(user);
        return Results.Ok(new { displayName = user.DisplayName ?? user.UserName });
    }

    private static async Task<IResult> ListPasskeysAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        var passkeys = await users.GetPasskeysAsync(user);
        return Results.Ok(passkeys.OrderBy(p => p.CreatedAt).Select(p => new
        {
            id = Base64Url.EncodeToString(p.CredentialId),
            name = p.Name ?? "Passkey",
            createdAt = p.CreatedAt.ToUnixTimeMilliseconds(),
            backedUp = p.IsBackedUp,
            transports = p.Transports ?? [],
        }));
    }

    private static async Task<IResult> PasskeyCreationOptionsAsync(
        ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        var optionsJson = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = user.Id,
            Name = user.UserName ?? user.Id,
            DisplayName = user.DisplayName ?? user.UserName ?? "SyncedNotes user",
        });
        return Results.Content(optionsJson, "application/json");
    }

    private static async Task<IResult> AddPasskeyAsync(
        AddPasskeyRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (string.IsNullOrEmpty(request.CredentialJson)) return ApiProblems.BadRequest("The browser didn't provide a passkey.");

        var existing = await users.GetPasskeysAsync(user);
        if (existing.Count >= 50) return ApiProblems.BadRequest("You have too many passkeys. Remove one first.");

        var attestation = await signIn.PerformPasskeyAttestationAsync(request.CredentialJson);
        if (!attestation.Succeeded)
        {
            return ApiProblems.BadRequest("The passkey couldn't be added.", attestation.Failure?.Message);
        }
        if (attestation.UserEntity.Id != user.Id)
        {
            return ApiProblems.BadRequest("The passkey was created for a different account.");
        }

        attestation.Passkey.Name = AuthEndpoints.CleanPasskeyName(request.Name);
        var result = await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey);
        return result.Succeeded
            ? Results.Ok(new { id = Base64Url.EncodeToString(attestation.Passkey.CredentialId), name = attestation.Passkey.Name })
            : ApiProblems.BadRequest("The passkey couldn't be saved.");
    }

    private static async Task<IResult> RenamePasskeyAsync(
        string id, RenamePasskeyRequest request, ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (!TryDecodeCredentialId(id, out var credentialId)) return ApiProblems.BadRequest("Invalid passkey id.");
        var passkey = await users.GetPasskeyAsync(user, credentialId);
        if (passkey is null) return ApiProblems.NotFound("Passkey not found.");

        passkey.Name = AuthEndpoints.CleanPasskeyName(request.Name);
        var result = await users.AddOrUpdatePasskeyAsync(user, passkey);
        return result.Succeeded ? Results.Ok(new { id, name = passkey.Name }) : ApiProblems.BadRequest("Couldn't rename the passkey.");
    }

    private static async Task<IResult> DeletePasskeyAsync(string id, ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (!TryDecodeCredentialId(id, out var credentialId)) return ApiProblems.BadRequest("Invalid passkey id.");

        var passkeys = await users.GetPasskeysAsync(user);
        if (!passkeys.Any(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)))
        {
            return ApiProblems.NotFound("Passkey not found.");
        }
        if (passkeys.Count <= 1)
        {
            return ApiProblems.BadRequest("You can't remove your only passkey.", "Add another passkey first so you can still sign in.");
        }

        var result = await users.RemovePasskeyAsync(user, credentialId);
        return result.Succeeded ? Results.NoContent() : ApiProblems.BadRequest("Couldn't remove the passkey.");
    }

    private static async Task<IResult> UpdateAppLockAsync(
        AppLockSettingsRequest request, ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (!AllowedTimeouts.Contains(request.TimeoutMinutes))
        {
            return ApiProblems.BadRequest("Unsupported auto-lock delay.");
        }

        user.AppLockEnabled = request.Enabled;
        user.AppLockTimeoutMinutes = request.TimeoutMinutes;
        user.AppLockOnLaunch = request.LockOnLaunch;
        await users.UpdateAsync(user);
        return Results.Ok(SessionEndpoints.LockSettings(user));
    }

    private static async Task<IResult> SetPinAsync(
        PinRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, IPasswordHasher<AppUser> hasher)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        var pin = request.Pin?.Trim() ?? "";
        if (!PinRegex().IsMatch(pin))
        {
            return ApiProblems.BadRequest("A PIN must be 4 to 12 digits.");
        }

        user.AppLockPinHash = hasher.HashPassword(user, pin);
        user.AppLockPinFailures = 0;
        await users.UpdateAsync(user);
        return Results.Ok(SessionEndpoints.LockSettings(user));
    }

    private static async Task<IResult> RemovePinAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        user.AppLockPinHash = null;
        user.AppLockPinFailures = 0;
        await users.UpdateAsync(user);
        return Results.Ok(SessionEndpoints.LockSettings(user));
    }

    private static async Task<IResult> CreateTokenAsync(
        CreateTokenRequest request, ClaimsPrincipal principal, ApiTokenService tokens, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) name = "Integration";
        if (name.Length > 100) name = name[..100];
        if (await tokens.CountAsync(userId, ct) >= ApiTokenService.MaxTokensPerUser)
        {
            return ApiProblems.BadRequest("You have too many tokens. Revoke one first.");
        }

        var (entity, token) = await tokens.CreateAsync(userId, name, ct);
        return Results.Ok(new { entity.Id, entity.Name, entity.Prefix, entity.CreatedAt, token });
    }

    private static async Task<IResult> DeleteAccountAsync(
        DeleteAccountRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return ApiProblems.Unauthorized("Not signed in.");
        if (!string.Equals(request.ConfirmUserName?.Trim(), user.UserName, StringComparison.Ordinal))
        {
            return ApiProblems.BadRequest("Type your username exactly to confirm.");
        }

        var userId = user.Id;
        await db.Notes.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Binders.Where(b => b.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Attachments.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ApiTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.RecoveryCodes.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded) return ApiProblems.BadRequest("The account couldn't be deleted.");

        await signIn.SignOutAsync();
        return Results.Ok(new { redirect = "/Account/Login?deleted=true" });
    }

    private static bool TryDecodeCredentialId(string id, out byte[] credentialId)
    {
        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
            return credentialId.Length > 0;
        }
        catch (FormatException)
        {
            credentialId = [];
            return false;
        }
    }

    [GeneratedRegex(@"^\d{4,12}$")]
    private static partial Regex PinRegex();
}
