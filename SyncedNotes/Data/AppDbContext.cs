using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SyncedNotes.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Binder> Binders => Set<Binder>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<UserState> UserStates => Set<UserState>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Note>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.UserId).IsRequired();
            e.Property(n => n.Title).HasMaxLength(NoteLimits.MaxTitleLength);
            e.Property(n => n.Color).HasMaxLength(20);
            e.Property(n => n.Icon).HasMaxLength(NoteLimits.MaxIconLength);
            e.HasIndex(n => new { n.UserId, n.Rev });
            e.HasIndex(n => new { n.UserId, n.BinderId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Binder>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.UserId).IsRequired();
            e.Property(b => b.Name).HasMaxLength(NoteLimits.MaxBinderNameLength);
            e.Property(b => b.Color).HasMaxLength(20);
            e.Property(b => b.Icon).HasMaxLength(NoteLimits.MaxIconLength);
            e.HasIndex(b => new { b.UserId, b.Rev });
            e.HasOne<AppUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Attachment>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.FileName).HasMaxLength(255);
            e.Property(a => a.ContentType).HasMaxLength(100);
            e.HasIndex(a => a.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ApiToken>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.Prefix).HasMaxLength(16);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RecoveryCode>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.CodeHash).HasMaxLength(64);
            e.HasIndex(r => r.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserState>(e =>
        {
            e.HasKey(s => s.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserState>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public static class NoteLimits
{
    public const int MaxTitleLength = 300;
    public const int MaxContentLength = 2_000_000;
    public const int MaxTags = 30;
    public const int MaxTagLength = 50;
    public const int MaxIconLength = 40;
    public const int MaxBinderNameLength = 60;
    public const int MaxPreferencesLength = 16_384;
    public const int MaxChangesPerPush = 500;

    /// <summary>Colours a note or binder may use. Kept in sync with the client palette.</summary>
    public static readonly HashSet<string> Colors =
        ["red", "orange", "yellow", "green", "teal", "blue", "indigo", "purple", "pink", "brown", "gray"];
}
