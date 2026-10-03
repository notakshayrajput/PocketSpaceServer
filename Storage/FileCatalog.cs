using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Storage;

// File mutations hold the account operation lock; listings can run alongside uploads.
public sealed class FileCatalog(ApplicationDbContext db, UserStorage paths, StorageManager stores, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Owner(ClaimsPrincipal user) => user.FindFirst("sub")!.Value;
    private static string Key(string path, string backend) => backend == "FileSystem" && OperatingSystem.IsWindows()
        ? path.ToUpperInvariant() : path;
    private string Relative(ClaimsPrincipal user, string path) => Path.GetRelativePath(paths.Root(user), path).Replace('\\', '/');
    private IQueryable<FileRecord> Active(ClaimsPrincipal user, string backend) => db.Files
        .Where(f => f.UserId == Owner(user) && f.Backend == backend && f.TrashEntryId == null);

    public async Task<List<FileRecord>> IndexAsync(ClaimsPrincipal user, IEnumerable<StorageItem> items)
    {
        var backend = await stores.CurrentAsync();
        var selected = items.Select(item => new { Item = item, RelativePath = Relative(user, item.Path) })
            .Select(item => new { item.Item, item.RelativePath, PathKey = Key(item.RelativePath, backend.Kind) }).ToArray();
        var byPath = new Dictionary<string, FileRecord>();
        foreach (var keys in selected.Select(item => item.PathKey).Distinct().Chunk(500))
            foreach (var record in await Active(user, backend.Kind).Where(file => keys.Contains(file.PathKey)).ToListAsync())
                byPath.Add(record.PathKey, record);
        var result = new List<FileRecord>();
        foreach (var item in selected)
        {
            if (!byPath.TryGetValue(item.PathKey, out var record))
            {
                record = new FileRecord { UserId = Owner(user), Backend = backend.Kind,
                    RelativePath = item.RelativePath, PathKey = item.PathKey,
                    IsFolder = item.Item.IsFolder, RecentAt = item.Item.LastModified };
                db.Files.Add(record);
                byPath.Add(item.PathKey, record);
            }
            result.Add(record);
        }
        await db.SaveChangesAsync();
        return result;
    }

    public async Task<FileSystemEntry> DescribeAsync(ClaimsPrincipal user, FileRecord record)
    {
        var backend = await stores.ForKindAsync(record.Backend);
        var item = await backend.StatAsync(paths.Resolve(user, record.RelativePath))
            ?? throw new FileNotFoundException();
        return Describe(record, item);
    }

    public static FileSystemEntry Describe(FileRecord record, StorageItem item)
    {
        return new FileSystemEntry { Id = record.Id, Name = item.Name, RelativePath = record.RelativePath,
            IsFolder = record.IsFolder, IsFavorite = record.IsFavorite, RecentAt = record.RecentAt,
            LastModified = item.LastModified, CreatedAt = item.CreatedAt, Size = item.Size };
    }

    public async Task<HomeFiles> HomeAsync(ClaimsPrincipal user)
    {
        var backend = await stores.CurrentAsync();
        var items = backend is S3StorageBackend s3
            ? await s3.ActiveFilesAsync(paths.Root(user))
            : (await backend.TreeAsync(paths.Root(user))).Where(item => !item.IsFolder).ToArray();
        var files = await IndexAsync(user, items);
        var byPath = items.ToDictionary(item => Relative(user, item.Path));
        var favorites = new List<FileSystemEntry>();
        foreach (var file in files.Where(f => f.IsFavorite).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            favorites.Add(Describe(file, byPath[file.RelativePath]));
        var recent = new List<FileSystemEntry>();
        foreach (var file in files.OrderByDescending(f => f.RecentAt).ThenBy(f => f.RelativePath).Take(20))
            recent.Add(Describe(file, byPath[file.RelativePath]));
        return new HomeFiles(favorites.ToArray(), recent.ToArray());
    }

    public async Task<bool> FavoriteAsync(ClaimsPrincipal user, string id, bool favorite)
    {
        var backend = await stores.CurrentAsync();
        var file = await Active(user, backend.Kind).SingleOrDefaultAsync(f => f.Id == id && !f.IsFolder);
        if (file is null || await backend.StatAsync(paths.Resolve(user, file.RelativePath)) is null) return false;
        file.IsFavorite = favorite;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task TouchAsync(ClaimsPrincipal user, IEnumerable<string> pathsToTouch)
    {
        var backend = await stores.CurrentAsync();
        var files = new List<StorageItem>();
        foreach (var path in pathsToTouch.Distinct())
        {
            var item = await backend.StatAsync(path);
            if (item is null) continue;
            if (item.IsFolder) files.AddRange((await backend.TreeAsync(path)).Where(child => !child.IsFolder));
            else files.Add(item);
        }
        foreach (var record in await IndexAsync(user, files)) record.RecentAt = Now;
        await db.SaveChangesAsync();
    }

    public async Task RenameAsync(ClaimsPrincipal user, string source, string destination)
    {
        var backend = await stores.CurrentAsync();
        var item = await backend.StatAsync(source) ?? throw new FileNotFoundException();
        IEnumerable<StorageItem> tree = item.IsFolder ? (await backend.TreeAsync(source)).Prepend(item) : [item];
        await IndexAsync(user, tree);
        var from = Relative(user, source);
        var to = Relative(user, destination);
        var fromKey = Key(from, backend.Kind);
        var active = await Active(user, backend.Kind).ToListAsync();
        var records = active.Where(f => f.PathKey == fromKey || f.PathKey.StartsWith(fromKey + "/", StringComparison.Ordinal)).ToArray();
        var destinationKey = Key(to, backend.Kind);
        var stale = active.Where(f => !records.Contains(f) &&
            (f.PathKey == destinationKey || f.PathKey.StartsWith(destinationKey + "/", StringComparison.Ordinal))).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Files.RemoveRange(stale);
        await db.SaveChangesAsync();
        foreach (var record in records)
        {
            record.RelativePath = to + record.RelativePath[from.Length..];
            record.PathKey = Key(record.RelativePath, backend.Kind);
            record.RecentAt = Now;
        }
        await db.SaveChangesAsync();
        await backend.MoveAsync(source, destination, item.IsFolder);
        try { await transaction.CommitAsync(); }
        catch { await backend.MoveAsync(destination, source, item.IsFolder); throw; }
    }

    public async Task TrashAsync(ClaimsPrincipal user, string path)
    {
        var backend = await stores.CurrentAsync();
        var item = await backend.StatAsync(path) ?? throw new FileNotFoundException();
        IEnumerable<StorageItem> tree = item.IsFolder ? (await backend.TreeAsync(path)).Prepend(item) : [item];
        var records = await IndexAsync(user, tree);
        var now = Now;
        var entry = new TrashEntry { UserId = Owner(user), Backend = backend.Kind,
            OriginalPath = Relative(user, path), IsFolder = item.IsFolder,
            Size = tree.Where(child => !child.IsFolder).Sum(child => child.Size),
            TrashedAt = now, ExpiresAt = now.AddDays(7) };
        db.TrashEntries.Add(entry);
        foreach (var record in records) record.TrashEntryId = entry.Id;
        await db.SaveChangesAsync();
        await FinishAsync(user, entry);
    }

    public async Task<List<TrashEntry>> TrashListAsync(ClaimsPrincipal user)
    {
        var backend = await stores.CurrentAsync();
        return await db.TrashEntries.Where(t => t.UserId == Owner(user) && t.Backend == backend.Kind &&
            t.State == TrashState.Trashed).OrderByDescending(t => t.TrashedAt).ToListAsync();
    }

    public async Task<bool> PurgeAsync(ClaimsPrincipal user, string id)
    {
        var backend = await stores.CurrentAsync();
        var entry = await db.TrashEntries.SingleOrDefaultAsync(t => t.UserId == Owner(user) &&
            t.Backend == backend.Kind && t.Id == id);
        if (entry is null || entry.State is not (TrashState.Trashed or TrashState.Purging)) return false;
        entry.State = TrashState.Purging;
        await db.SaveChangesAsync();
        await FinishAsync(user, entry);
        return true;
    }

    public async Task<RestoreResult> RestoreAsync(ClaimsPrincipal user, string id)
    {
        var backend = await stores.CurrentAsync();
        var entry = await db.TrashEntries.SingleOrDefaultAsync(t => t.UserId == Owner(user) &&
            t.Backend == backend.Kind && t.Id == id);
        if (entry is null) return RestoreResult.Missing;
        if (entry.ExpiresAt <= Now || entry.State == TrashState.Purging) return RestoreResult.Expired;
        var destination = paths.Resolve(user, entry.OriginalPath);
        if (await backend.StatAsync(destination) is not null) return RestoreResult.Conflict;
        if ((await backend.StatAsync(Path.GetDirectoryName(destination)!))?.IsFolder != true)
            return RestoreResult.ParentMissing;
        entry.State = TrashState.Restoring;
        await db.SaveChangesAsync();
        await FinishAsync(user, entry);
        return RestoreResult.Restored;
    }

    public async Task RecoverAsync(ClaimsPrincipal user)
    {
        var backend = await stores.CurrentAsync();
        foreach (var entry in await db.TrashEntries.Where(t => t.UserId == Owner(user) &&
            t.Backend == backend.Kind &&
            (t.State == TrashState.Moving || t.State == TrashState.Restoring)).ToListAsync())
            await FinishAsync(user, entry);
    }

    public async Task CleanupAsync(ClaimsPrincipal user, ILogger logger, CancellationToken cancellationToken)
    {
        var now = Now;
        var entries = await db.TrashEntries.Where(t => t.UserId == Owner(user) &&
            (t.ExpiresAt <= now || t.State != TrashState.Trashed)).ToListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.State != TrashState.Trashed) await FinishAsync(user, entry);
                if (db.Entry(entry).State == EntityState.Detached || entry.ExpiresAt > Now) continue;
                entry.State = TrashState.Purging;
                await db.SaveChangesAsync(cancellationToken);
                await FinishAsync(user, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogError(ex, "Could not clean up trash entry {TrashId}; will retry.", entry.Id);
            }
        }
    }

    private async Task FinishAsync(ClaimsPrincipal user, TrashEntry entry)
    {
        var backend = await stores.ForKindAsync(entry.Backend);
        var trashPath = paths.TrashPath(user, entry.Id);
        if (entry.State == TrashState.Moving)
        {
            var source = paths.Resolve(user, entry.OriginalPath);
            if (await backend.StatAsync(source) is not null)
            {
                await backend.CreateDirectoryAsync(Path.GetDirectoryName(trashPath)!);
                await backend.MoveAsync(source, trashPath, entry.IsFolder);
            }
            if (await backend.StatAsync(trashPath) is null) throw new IOException("Trash item is unavailable.");
            entry.State = TrashState.Trashed;
            await db.SaveChangesAsync();
        }
        else if (entry.State == TrashState.Restoring)
        {
            var destination = paths.Resolve(user, entry.OriginalPath);
            if (await backend.StatAsync(trashPath) is not null)
            {
                if (await backend.StatAsync(destination) is not null ||
                    (await backend.StatAsync(Path.GetDirectoryName(destination)!))?.IsFolder != true)
                {
                    entry.State = TrashState.Trashed;
                    await db.SaveChangesAsync();
                    throw new IOException("The original location is unavailable.");
                }
                await backend.MoveAsync(trashPath, destination, entry.IsFolder);
            }
            if (await backend.StatAsync(destination) is null) throw new IOException("The restored file is unavailable.");
            var records = await db.Files.Where(f => f.TrashEntryId == entry.Id).ToListAsync();
            var keys = records.Select(f => f.PathKey).ToArray();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await Active(user, entry.Backend).Where(f => keys.Contains(f.PathKey)).ExecuteDeleteAsync();
            foreach (var record in records) { record.TrashEntryId = null; record.RecentAt = Now; }
            await db.SaveChangesAsync();
            db.TrashEntries.Remove(entry);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else if (entry.State == TrashState.Purging)
        {
            await backend.DeleteAsync(trashPath, entry.IsFolder);
            db.TrashEntries.Remove(entry);
            await db.SaveChangesAsync();
        }
    }
}

public sealed record HomeFiles(FileSystemEntry[] Favorites, FileSystemEntry[] Recent);
public enum RestoreResult { Restored, Missing, Expired, Conflict, ParentMissing }
