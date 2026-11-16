using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;
using SyncedNotes.Infrastructure;

namespace SyncedNotes.Auth;

/// <summary>
/// One-time recovery codes for passkey-only accounts. Codes are 50-bit random values shown to the user once;
/// only their SHA-256 hashes are stored.
/// </summary>
public sealed class RecoveryCodeService(AppDbContext db, TimeProvider time)
{
    // Crockford-style alphabet without easily confused characters (0/O, 1/I/L, U).
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int DefaultCount = 10;

    public async Task<IReadOnlyList<string>> GenerateAsync(string userId, int count = DefaultCount, CancellationToken ct = default)
    {
        await db.RecoveryCodes.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);

        var now = time.NowMs();
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var code = CreateCode();
            codes.Add(code);
            db.RecoveryCodes.Add(new RecoveryCode { UserId = userId, CodeHash = Hash(code), CreatedAt = now });
        }

        await db.SaveChangesAsync(ct);
        return codes;
    }

    public Task<int> CountRemainingAsync(string userId, CancellationToken ct = default) =>
        db.RecoveryCodes.CountAsync(r => r.UserId == userId && r.UsedAt == null, ct);

    /// <summary>Marks a matching unused code as used. Returns false when the code is wrong or already used.</summary>
    public async Task<bool> RedeemAsync(string userId, string code, CancellationToken ct = default)
    {
        var normalized = Normalize(code);
        if (normalized.Length != 10)
        {
            return false;
        }

        var hash = Hash(normalized);
        var candidates = await db.RecoveryCodes.Where(r => r.UserId == userId && r.UsedAt == null).ToListAsync(ct);
        var match = candidates.FirstOrDefault(r =>
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.CodeHash), Encoding.ASCII.GetBytes(hash)));
        if (match is null)
        {
            return false;
        }

        match.UsedAt = time.NowMs();
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string CreateCode()
    {
        Span<char> chars = stackalloc char[10];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return $"{new string(chars[..5])}-{new string(chars[5..])}";
    }

    private static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        return sb.ToString();
    }

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}
