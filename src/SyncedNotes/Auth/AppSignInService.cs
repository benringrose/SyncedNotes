using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using SyncedNotes.Data;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace SyncedNotes.Auth;

public static class AppClaims
{
    /// <summary>Present (value "1") while the app lock is engaged for this browser session.</summary>
    public const string Locked = "sn_locked";

    /// <summary>Authentication method reference: "pk" (passkey) or "recovery".</summary>
    public const string AuthMethod = "amr";
}

/// <summary>
/// Signs users in with passkeys. Unlike <see cref="SignInManager{TUser}.PasskeySignInAsync"/>, this keeps the
/// session persistent (an installed PWA should stay signed in) and manages the app-lock claim.
/// </summary>
public sealed class AppSignInService(
    SignInManager<AppUser> signInManager,
    UserManager<AppUser> userManager,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AppSignInService> logger)
{
    /// <summary>
    /// Verifies a newly created passkey. Returns null when no registration ceremony is in progress (it was never
    /// started or its state expired), which SignInManager reports by throwing.
    /// </summary>
    public async Task<PasskeyAttestationResult?> AttestAsync(string credentialJson)
    {
        try
        {
            return await signInManager.PerformPasskeyAttestationAsync(credentialJson);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("Passkey attestation without a ceremony in progress: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Verifies a passkey assertion. Returns null when no sign-in ceremony is in progress.</summary>
    public async Task<PasskeyAssertionResult<AppUser>?> AssertAsync(string credentialJson)
    {
        try
        {
            return await signInManager.PerformPasskeyAssertionAsync(credentialJson);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("Passkey assertion without a ceremony in progress: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>Signs in with a passkey. Result is null when no sign-in ceremony was in progress.</summary>
    public async Task<(SignInResult? Result, AppUser? User)> PasskeySignInAsync(string credentialJson, bool persistent)
    {
        var assertion = await AssertAsync(credentialJson);
        if (assertion is null)
        {
            return (null, null);
        }
        if (!assertion.Succeeded)
        {
            logger.LogInformation("Passkey assertion failed: {Message}", assertion.Failure?.Message);
            return (SignInResult.Failed, null);
        }

        var user = assertion.User;
        if (!await signInManager.CanSignInAsync(user))
        {
            return (SignInResult.NotAllowed, user);
        }
        // Note: account lockout (from failed recovery-code attempts) intentionally does not block passkey
        // sign-in. A passkey can't be brute-forced, and otherwise anyone who knows the username could lock the
        // owner out of their own notes by guessing recovery codes.

        // Store the updated sign count / backup state reported by the authenticator.
        var update = await userManager.AddOrUpdatePasskeyAsync(user, assertion.Passkey);
        if (!update.Succeeded)
        {
            return (SignInResult.Failed, user);
        }

        await SignInAsync(user, persistent, "pk", locked: false);
        return (SignInResult.Success, user);
    }

    public Task SignInAsync(AppUser user, bool persistent, string method, bool locked)
    {
        var claims = new List<Claim> { new(AppClaims.AuthMethod, method) };
        if (locked)
        {
            claims.Add(new Claim(AppClaims.Locked, "1"));
        }

        return signInManager.SignInWithClaimsAsync(user, new AuthenticationProperties { IsPersistent = persistent }, claims);
    }

    /// <summary>Re-issues the authentication cookie with or without the app-lock claim.</summary>
    public async Task SetLockedAsync(AppUser user, bool locked)
    {
        var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        var current = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var persistent = current.Properties?.IsPersistent ?? false;
        var method = current.Principal?.FindFirstValue(AppClaims.AuthMethod) ?? "pk";
        await SignInAsync(user, persistent, method, locked);
    }
}
