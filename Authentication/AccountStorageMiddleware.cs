using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Authentication;

public sealed class AccountStorageMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccountOperationLocks operations, ApplicationDbContext db, TimeProvider clock, FileCatalog catalog)
    {
        var path = context.Request.Path;
        var id = context.User.FindFirst("sub")?.Value;
        if (id is null || !(path.StartsWithSegments("/api/space") || path.StartsWithSegments("/api/upload") ||
            path.StartsWithSegments("/api/download")))
        {
            await next(context);
            return;
        }

        using var lease = await operations.AcquireAsync(id, context.RequestAborted);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, context.RequestAborted);
        if (user is null || !user.CanAccess(clock.GetUtcNow().UtcDateTime))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (user.Status == AccountStatus.Pending)
        {
            var remaining = user.PendingExpiresAt!.Value - clock.GetUtcNow().UtcDateTime;
            if (remaining <= TimeSpan.Zero) lifetime.Cancel();
            else lifetime.CancelAfter(remaining);
        }
        var originalCancellation = context.RequestAborted;
        context.RequestAborted = lifetime.Token;
        try
        {
            // Finish interrupted trash/restore operations before allowing path reuse.
            try { await catalog.RecoverAsync(context.User); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { message = "A file operation is still pending. Please try again." });
                return;
            }
            await next(context);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { context.Abort(); }
        finally { context.RequestAborted = originalCancellation; }
    }
}
