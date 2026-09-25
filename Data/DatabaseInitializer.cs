using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Admin", "User" })
        {
            if (!await roles.RoleExistsAsync(role))
                EnsureSuccess(await roles.CreateAsync(new IdentityRole(role)));
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        // Seed once. Subsequent startups must never reset an existing password or role.
        if (await users.FindByNameAsync("admin") is not null)
            return;

        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var admin = new ApplicationUser { UserName = "admin", Status = AccountStatus.Approved, CreatedAt = now, ApprovedAt = now };
        EnsureSuccess(await users.CreateAsync(admin, configuration["BootstrapAdmin:Password"] ?? "admin@123"));
        EnsureSuccess(await users.AddToRoleAsync(admin, "Admin"));
        await transaction.CommitAsync();
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
