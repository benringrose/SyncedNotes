using Microsoft.EntityFrameworkCore;
using SyncedNotes.Data;

namespace SyncedNotes.Auth;

public enum RegistrationMode
{
    /// <summary>Only the very first account can be created (the default for a personal notes server).</summary>
    FirstUserOnly,
    /// <summary>Anyone can create an account. Every account only ever sees its own notes.</summary>
    Open,
    /// <summary>No new accounts can be created.</summary>
    Closed,
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public RegistrationMode Registration { get; set; } = RegistrationMode.FirstUserOnly;

    /// <summary>How long a signed-in browser stays signed in without being used.</summary>
    public int SessionDays { get; set; } = 60;
}

public sealed class RegistrationPolicy(AppDbContext db, Microsoft.Extensions.Options.IOptions<AuthOptions> options)
{
    /// <summary>Serialises account creation so "first user only" cannot be raced.</summary>
    public static readonly SemaphoreSlim CreationLock = new(1, 1);

    public async Task<bool> IsOpenAsync(CancellationToken ct = default) => options.Value.Registration switch
    {
        RegistrationMode.Open => true,
        RegistrationMode.Closed => false,
        _ => !await db.Users.AnyAsync(ct),
    };
}
