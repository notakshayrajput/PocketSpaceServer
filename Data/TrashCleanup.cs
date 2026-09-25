using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Data;

public sealed class TrashCleanup(ApplicationDbContext db, FileCatalog catalog, UserManager<ApplicationUser> users,
    AccountOperationLocks operations, ILogger<TrashCleanup> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.TrashEntries.Select(t => t.UserId).Distinct().ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            using var lease = await operations.AcquireAsync(id, cancellationToken);
            var user = await users.FindByIdAsync(id);
            if (user is null || user.Status == AccountStatus.Deleting) continue;
            var identity = new ClaimsIdentity("cleanup", "name", "role");
            identity.AddClaim(new Claim("sub", id));
            foreach (var role in await users.GetRolesAsync(user)) identity.AddClaim(new Claim("role", role));
            await catalog.CleanupAsync(new ClaimsPrincipal(identity), logger, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }
}

public sealed class TrashCleanupWorker(IServiceScopeFactory scopes, ILogger<TrashCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TrashCleanup>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Trash cleanup failed; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
