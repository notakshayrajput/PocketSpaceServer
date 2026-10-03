using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;

namespace PocketSpaceServer.Storage;

public sealed class QuotaUsage(UserStorage storage, StorageManager stores, ApplicationDbContext db)
{
    public async Task<long> UsedBytesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var backend = await stores.CurrentAsync();
        var activeBytes = backend is S3StorageBackend s3
            ? await s3.ActiveBytesAsync(storage.Root(user), cancellationToken)
            : (await backend.TreeAsync(storage.Root(user), cancellationToken))
                .Where(item => !item.IsFolder).Sum(item => item.Size);
        var id = user.FindFirst("sub")!.Value;
        var trashBytes = await db.TrashEntries.Where(entry => entry.UserId == id && entry.Backend == backend.Kind)
            .SumAsync(entry => entry.Size, cancellationToken);
        return checked(activeBytes + trashBytes);
    }
}
