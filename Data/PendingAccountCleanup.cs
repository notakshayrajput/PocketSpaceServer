using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Data;

public sealed class PendingAccountCleanup(ApplicationDbContext db, UserStorage storage,
    AccountOperationLocks operations, TimeProvider clock, ILogger<PendingAccountCleanup> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.Users.Where(u => u.Status == AccountStatus.Deleting ||
            (u.Status == AccountStatus.Pending && u.PendingExpiresAt <= now))
            .Select(u => u.Id).ToListAsync(cancellationToken);

        foreach (var id in ids)
        {
            using var lease = await operations.AcquireAsync(id, cancellationToken);
            try
            {
                // Recheck after taking the same lock used by approval and file operations.
                var claimed = await db.Users.Where(u => u.Id == id && (u.Status == AccountStatus.Deleting ||
                    (u.Status == AccountStatus.Pending && u.PendingExpiresAt <= now)))
                    .ExecuteUpdateAsync(update => update.SetProperty(u => u.Status, AccountStatus.Deleting), cancellationToken);
                if (claimed == 0) continue;

                // The only deletion target is the generated GUID folder under the managed user root.
                var folder = storage.UserRoot(id);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                // Keep the Deleting record if storage cleanup fails, so the next run can retry.
                await db.Users.Where(u => u.Id == id && u.Status == AccountStatus.Deleting)
                    .ExecuteDeleteAsync(cancellationToken);
                logger.LogInformation("Removed expired unapproved account {AccountId} and its storage.", id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogError(ex, "Could not clean up expired account {AccountId}; will retry.", id);
            }
        }
    }
}

public sealed class PendingAccountCleanupWorker(IServiceScopeFactory scopes, ILogger<PendingAccountCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PendingAccountCleanup>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Pending-account cleanup failed; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
