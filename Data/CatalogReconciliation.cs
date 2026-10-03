using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Authentication;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Data;

// One initial scan per account and backend makes existing unmanaged files visible.
// Later requests use the database; the worker refreshes changes made outside the app.
public sealed class CatalogReadiness
{
    internal readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    internal readonly ConcurrentDictionary<string, byte> Ready = new();
}

public sealed class CatalogReconciler(
    CatalogReadiness readiness, AccountOperationLocks operations, FileCatalog catalog,
    StorageOptions options)
{
    public Task EnsureReadyAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        ReconcileAsync(user, force: false, cancellationToken);

    public Task RefreshAsync(ClaimsPrincipal user, CancellationToken cancellationToken) =>
        ReconcileAsync(user, force: true, cancellationToken);

    private async Task ReconcileAsync(ClaimsPrincipal user, bool force, CancellationToken cancellationToken)
    {
        var id = user.FindFirst("sub")!.Value;
        var key = options.Provider + ":" + id;
        if (!force && readiness.Ready.ContainsKey(key)) return;
        var gate = readiness.Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && readiness.Ready.ContainsKey(key)) return;
            using var lease = await operations.AcquireAsync(id, cancellationToken);
            await catalog.RecoverAsync(user);
            await catalog.ReconcileAsync(user, cancellationToken);
            readiness.Ready[key] = 1;
        }
        finally { gate.Release(); }
    }
}

public sealed class CatalogReconciliationWorker(IServiceScopeFactory scopes,
    ILogger<CatalogReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                using var listingScope = scopes.CreateScope();
                var db = listingScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var now = listingScope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
                var ids = await db.Users.AsNoTracking()
                    .Where(u => u.Status == AccountStatus.Approved ||
                        (u.Status == AccountStatus.Pending && u.PendingExpiresAt > now))
                    .Select(u => u.Id).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                        var account = await users.FindByIdAsync(id);
                        if (account is null || !account.CanAccess(now)) continue;
                        var identity = new ClaimsIdentity("catalog", "name", "role");
                        identity.AddClaim(new Claim("sub", id));
                        foreach (var role in await users.GetRolesAsync(account))
                            identity.AddClaim(new Claim("role", role));
                        await scope.ServiceProvider.GetRequiredService<CatalogReconciler>()
                            .RefreshAsync(new ClaimsPrincipal(identity), stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Could not reconcile file catalog for account {AccountId}.", id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "File catalog reconciliation failed; will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
