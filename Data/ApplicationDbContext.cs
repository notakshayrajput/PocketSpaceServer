using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
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
