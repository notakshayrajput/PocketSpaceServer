using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;

namespace PocketSpaceServer.Storage;

public sealed class QuotaUsage(UserStorage storage, ApplicationDbContext db)
{
    public async Task<long> UsedBytesAsync(ClaimsPrincipal user)
    {
        var activeBytes = UserStorage.FilesRecursively(storage.Root(user))
            .Sum(path => new FileInfo(path).Length);
        var id = user.FindFirst("sub")!.Value;
        var trashBytes = await db.TrashEntries.Where(entry => entry.UserId == id)
            .SumAsync(entry => entry.Size);
        return checked(activeBytes + trashBytes);
    }
}
