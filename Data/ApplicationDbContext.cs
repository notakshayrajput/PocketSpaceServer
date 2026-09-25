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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<FileRecord>().HasOne<ApplicationUser>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<FileRecord>().HasOne<TrashEntry>().WithMany().HasForeignKey(f => f.TrashEntryId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<FileRecord>().HasIndex(f => new { f.UserId, f.PathKey }).IsUnique().HasFilter("\"TrashEntryId\" IS NULL");
        builder.Entity<FileRecord>().HasIndex(f => new { f.UserId, f.RecentAt });
        builder.Entity<FileRecord>().Property(f => f.RecentAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        builder.Entity<TrashEntry>().HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<TrashEntry>().HasIndex(t => new { t.UserId, t.ExpiresAt });
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
