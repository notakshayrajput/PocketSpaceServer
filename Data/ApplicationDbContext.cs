using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<TrashEntry> TrashEntries => Set<TrashEntry>();
    public DbSet<StorageConfiguration> StorageConfigurations => Set<StorageConfiguration>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ApplicationUser>().Property(u => u.QuotaBytes).HasDefaultValue(UserQuota.DefaultBytes);
        builder.Entity<ApplicationUser>().Property(u => u.IsBlocked).HasDefaultValue(false);
        builder.Entity<FileRecord>().HasOne<ApplicationUser>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<FileRecord>().HasOne<TrashEntry>().WithMany().HasForeignKey(f => f.TrashEntryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<FileRecord>().HasIndex(f => new { f.Backend, f.UserId, f.PathKey }).IsUnique().HasFilter("\"TrashEntryId\" IS NULL");
        builder.Entity<FileRecord>().HasIndex(f => new { f.UserId, f.Backend, f.IsPresent, f.RecentAt });
        builder.Entity<FileRecord>().HasIndex(f => new { f.Backend, f.UserId, f.ParentPathKey, f.IsPresent, f.IsFolder, f.NameSortKey });
        builder.Entity<FileRecord>().HasIndex(f => new { f.Backend, f.UserId, f.ParentPathKey, f.IsPresent, f.IsFolder, f.Size });
        builder.Entity<FileRecord>().HasIndex(f => new { f.Backend, f.UserId, f.ParentPathKey, f.IsPresent, f.IsFolder, f.LastModified });
        builder.Entity<FileRecord>().HasIndex(f => new { f.Backend, f.UserId, f.ParentPathKey, f.IsPresent, f.IsFolder, f.CreatedAt });
        builder.Entity<FileRecord>().HasIndex(f => new { f.UserId, f.Backend, f.IsPresent, f.IsFavorite });
        builder.Entity<FileRecord>().Property(f => f.RecentAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<FileRecord>().Property(f => f.LastModified)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<FileRecord>().Property(f => f.CreatedAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<TrashEntry>().HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TrashEntry>().HasIndex(t => new { t.UserId, t.ExpiresAt });
        builder.Entity<StorageConfiguration>().HasData(new StorageConfiguration());
        builder.Entity<TrashEntry>().Property(t => t.TrashedAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<TrashEntry>().Property(t => t.ExpiresAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<ApplicationUser>().Property(u => u.PasswordResetRequestedAt)
            .HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
        // SQLite stores no time-zone marker; always serialize these timestamps as UTC.
        builder.Entity<ApplicationUser>().Property(u => u.CreatedAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<ApplicationUser>().Property(u => u.PendingExpiresAt)
            .HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
        builder.Entity<ApplicationUser>().Property(u => u.ApprovedAt)
            .HasConversion(value => value, value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value);
    }
}
