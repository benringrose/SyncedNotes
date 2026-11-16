using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncedNotes.Auth;

namespace SyncedNotes.Tests.Infrastructure;

public static class TestAuth
{
    /// <summary>Authenticates requests that carry <see cref="UserHeader"/>.</summary>
    public const string Scheme = "Test";

    /// <summary>Default scheme: the test scheme when the header is present, otherwise the Identity cookie.</summary>
    public const string PolicyScheme = "TestOrIdentityCookie";

    /// <summary>The user id to authenticate as (becomes the NameIdentifier claim).</summary>
    public const string UserHeader = "X-Test-User";

    /// <summary>"1" adds the app-lock claim (<see cref="AppClaims.Locked"/>), like a locked browser session.</summary>
    public const string LockedHeader = "X-Test-Locked";

    /// <summary>Overrides the connection's remote IP address (see <see cref="TestRemoteIpStartupFilter"/>).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-IP";

    /// <summary>The authentication method claim the test scheme issues.</summary>
    public const string AuthMethod = "test";
}

/// <summary>
/// Stands in for the passkey-issued Identity cookie: passkey ceremonies can't run in tests, so the principal is
/// built from request headers instead.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers[TestAuth.UserHeader].ToString();
        if (string.IsNullOrEmpty(userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(AppClaims.AuthMethod, TestAuth.AuthMethod),
        };
        if (Request.Headers[TestAuth.LockedHeader] == "1")
        {
            claims.Add(new Claim(AppClaims.Locked, "1"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
