using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;

namespace SyncedNotes.Auth;

public static class ApiTokenDefaults
{
    public const string Scheme = "ApiToken";
    public const string Policy = "ApiToken";
    public const string TokenPrefix = "snp_";
    public const string TokenIdClaim = "sn_token_id";
}

/// <summary>Creates, lists and revokes personal access tokens. Tokens are stored as SHA-256 hashes.</summary>
public sealed class ApiTokenService(AppDbContext db, TimeProvider time)
{
    public const int MaxTokensPerUser = 25;

    public async Task<(ApiToken Entity, string Token)> CreateAsync(string userId, string name, CancellationToken ct = default)
    {
        var token = ApiTokenDefaults.TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var entity = new ApiToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            TokenHash = Hash(token),
            Prefix = token[..10],
            CreatedAt = time.NowMs(),
        };
        db.ApiTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return (entity, token);
    }

    public Task<List<ApiToken>> ListAsync(string userId, CancellationToken ct = default) =>
        db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

    public Task<int> CountAsync(string userId, CancellationToken ct = default) =>
        db.ApiTokens.CountAsync(t => t.UserId == userId, ct);

    public async Task<bool> RevokeAsync(string userId, Guid id, CancellationToken ct = default) =>
        await db.ApiTokens.Where(t => t.UserId == userId && t.Id == id).ExecuteDeleteAsync(ct) > 0;

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Authenticates <c>Authorization: Bearer snp_...</c> requests for the integration API.</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(ApiTokenDefaults.TokenPrefix, StringComparison.Ordinal) || token.Length > 100)
        {
            return AuthenticateResult.Fail("Malformed token.");
        }

        var hash = ApiTokenService.Hash(token);
        var entity = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);
        if (entity is null)
        {
            return AuthenticateResult.Fail("Invalid or revoked token.");
        }

        // Record usage at most once a minute to avoid a write on every request.
        var now = time.NowMs();
        if (entity.LastUsedAt is null || now - entity.LastUsedAt > 60_000)
        {
            entity.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, entity.UserId),
            new Claim(ApiTokenDefaults.TokenIdClaim, entity.Id.ToString()),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
