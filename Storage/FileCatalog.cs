using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Storage;

// File mutations hold the account operation lock; listings can run alongside uploads.
public sealed class FileCatalog(ApplicationDbContext db, UserStorage paths, StorageManager stores,
    CatalogReadCache cache, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Owner(ClaimsPrincipal user) => user.FindFirst("sub")!.Value;
    private static string Key(string path, string backend) => backend == "FileSystem" && OperatingSystem.IsWindows()
        ? path.ToUpperInvariant() : path;
    private string Relative(ClaimsPrincipal user, string path) => Path.GetRelativePath(paths.Root(user), path).Replace('\\', '/');
    private static string Parent(string path)
    {
        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        return string.IsNullOrEmpty(parent) ? "." : parent;
    }
    private static void SetPath(FileRecord record, string path)
    {
        record.RelativePath = path;
        record.PathKey = Key(path, record.Backend);
        record.ParentPathKey = Key(Parent(path), record.Backend);
        record.Name = Path.GetFileName(path);
        record.NameSortKey = FolderListing.NaturalSortKey(record.Name);
        record.OrdinalNameSortKey = FolderListing.OrdinalSortKey(record.Name);
    }
    private static void SetMetadata(FileRecord record, StorageItem item)
    {
        record.IsFolder = item.IsFolder;
        record.IsPresent = true;
        record.Size = item.Size;
        record.LastModified = item.LastModified;
        record.CreatedAt = item.CreatedAt;
    }
    private IQueryable<FileRecord> Active(ClaimsPrincipal user, string backend) => db.Files
        .Where(f => f.UserId == Owner(user) && f.Backend == backend && f.TrashEntryId == null);
    private IQueryable<FileRecord> Visible(ClaimsPrincipal user, string backend) =>
        Active(user, backend).Where(f => f.IsPresent);

    public async Task<List<FileRecord>> IndexAsync(ClaimsPrincipal user, IEnumerable<StorageItem> items,
        CancellationToken cancellationToken = default)
    {
        var backend = await stores.CurrentAsync();
        var selected = items.Select(item => new { Item = item, RelativePath = Relative(user, item.Path) })
            .Select(item => new { item.Item, item.RelativePath, PathKey = Key(item.RelativePath, backend.Kind) }).ToArray();
        var byPath = new Dictionary<string, FileRecord>();
        foreach (var keys in selected.Select(item => item.PathKey).Distinct().Chunk(500))
            foreach (var record in await Active(user, backend.Kind).Where(file => keys.Contains(file.PathKey))
                .ToListAsync(cancellationToken))
                byPath.Add(record.PathKey, record);
        var result = new List<FileRecord>();
        foreach (var item in selected)
        {
            if (!byPath.TryGetValue(item.PathKey, out var record))
            {
                record = new FileRecord { UserId = Owner(user), Backend = backend.Kind,
                    RecentAt = item.Item.LastModified };
                db.Files.Add(record);
                byPath.Add(item.PathKey, record);
            }
            SetPath(record, item.RelativePath);
            SetMetadata(record, item.Item);
            result.Add(record);
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            cache.Invalidate(user, backend.Kind);
        }
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

    public static FileSystemEntry Describe(FileRecord record) => new()
    {
        Id = record.Id, Name = record.Name, RelativePath = record.RelativePath,
        IsFolder = record.IsFolder, IsFavorite = record.IsFavorite, RecentAt = record.RecentAt,
        LastModified = record.LastModified, CreatedAt = record.CreatedAt, Size = record.Size
    };

    public async Task<HomeFiles> HomeAsync(ClaimsPrincipal user)
    {
        var backend = await stores.CurrentAsync();
        return await cache.GetAsync(user, backend.Kind, "home", TimeSpan.FromSeconds(45), async () =>
        {
            var files = Visible(user, backend.Kind).AsNoTracking().Where(f => !f.IsFolder);
            var favorites = (await files.Where(f => f.IsFavorite).ToListAsync())
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(Describe).ToArray();
            var recent = (await files.OrderByDescending(f => f.RecentAt)
                .ThenBy(f => f.RelativePath).Take(20).ToListAsync()).Select(Describe).ToArray();
            return new HomeFiles(favorites, recent);
        });
    }

    public async Task<(FileSystemEntry[] Files, int Total)> ListPageAsync(ClaimsPrincipal user,
        string relativePath, string search, string sortBy, string direction, int offset, int limit)
    {
        var backend = await stores.CurrentAsync();
        var cacheKey = string.Join('\0', relativePath, search, sortBy, direction,
            offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await cache.GetAsync(user, backend.Kind, "list:" + cacheKey, TimeSpan.FromSeconds(45),
            () => LoadPageAsync(user, backend.Kind, relativePath, search, sortBy, direction, offset, limit));
    }

    private async Task<(FileSystemEntry[] Files, int Total)> LoadPageAsync(ClaimsPrincipal user,
        string backend, string relativePath, string search, string sortBy, string direction, int offset, int limit)
    {
        var query = Visible(user, backend).AsNoTracking()
            .Where(f => f.ParentPathKey == Key(relativePath, backend));
        if (!string.IsNullOrWhiteSpace(search))
        {
            // Preserve the existing typo-tolerant search. It scans only catalog
            // metadata; ordinary folder pages use indexed SQL ordering and paging.
            var matches = (await query.ToListAsync()).Where(f => FolderListing.MatchesName(f.Name, search)).ToArray();
            var byPath = matches.ToDictionary(f => f.RelativePath);
            var page = FolderListing.Sort(matches.Select(f => new FolderListing.Entry(
                    f.RelativePath, f.Name, f.IsFolder, f.Size, f.LastModified, f.CreatedAt)), sortBy, direction)
                .Skip(offset).Take(limit).Select(entry => Describe(byPath[entry.Path])).ToArray();
            return (page, matches.Length);
        }

        var total = await query.CountAsync();
        var foldersFirst = query.OrderByDescending(f => f.IsFolder);
        IOrderedQueryable<FileRecord> ordered = sortBy switch
        {
            "name" when direction == "desc" => foldersFirst.ThenByDescending(f => f.NameSortKey),
            "name" => foldersFirst.ThenBy(f => f.NameSortKey),
            "size" when direction == "desc" => foldersFirst.ThenByDescending(f => f.Size),
            "size" => foldersFirst.ThenBy(f => f.Size),
            "lastModified" when direction == "desc" => foldersFirst.ThenByDescending(f => f.LastModified),
            "lastModified" => foldersFirst.ThenBy(f => f.LastModified),
            "createdAt" when direction == "desc" => foldersFirst.ThenByDescending(f => f.CreatedAt),
            _ => foldersFirst.ThenBy(f => f.CreatedAt)
        };
        if (sortBy != "name") ordered = ordered.ThenBy(f => f.NameSortKey);
        var records = await ordered.ThenBy(f => f.OrdinalNameSortKey).Skip(offset).Take(limit).ToListAsync();
        return (records.Select(Describe).ToArray(), total);
    }

    public async Task ReconcileAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var backend = await stores.CurrentAsync();
        var items = await backend.TreeAsync(paths.Root(user), cancellationToken);
        var seen = new HashSet<string>();
        foreach (var batch in items.Chunk(500))
        {
            await IndexAsync(user, batch, cancellationToken);
            foreach (var item in batch) seen.Add(Key(Relative(user, item.Path), backend.Kind));
            db.ChangeTracker.Clear();
        }
        var active = await Active(user, backend.Kind).AsNoTracking()
            .Where(f => f.IsPresent).Select(f => new { f.Id, f.PathKey }).ToListAsync(cancellationToken);
        var stale = active.Where(f => !seen.Contains(f.PathKey)).Select(f => f.Id).ToArray();
        foreach (var ids in stale.Chunk(500))
            await Active(user, backend.Kind).Where(f => ids.Contains(f.Id))
                .ExecuteUpdateAsync(update => update.SetProperty(f => f.IsPresent, false), cancellationToken);
        if (stale.Length > 0)
        {
            cache.Invalidate(user, backend.Kind);
        }
    }

    public async Task<bool> FavoriteAsync(ClaimsPrincipal user, string id, bool favorite)
    {
        var backend = await stores.CurrentAsync();
        var file = await Active(user, backend.Kind).SingleOrDefaultAsync(f => f.Id == id && !f.IsFolder);
        if (file is null || await backend.StatAsync(paths.Resolve(user, file.RelativePath)) is null) return false;
        file.IsFavorite = favorite;
        await db.SaveChangesAsync();
        cache.Invalidate(user, backend.Kind);
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
        cache.Invalidate(user, backend.Kind);
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
            SetPath(record, to + record.RelativePath[from.Length..]);
            record.RecentAt = Now;
        }
        await db.SaveChangesAsync();
        await backend.MoveAsync(source, destination, item.IsFolder);
        try { await transaction.CommitAsync(); }
        catch { await backend.MoveAsync(destination, source, item.IsFolder); throw; }
        cache.Invalidate(user, backend.Kind);
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
        cache.Invalidate(user, backend.Kind);
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
        cache.Invalidate(user, backend.Kind);
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
        cache.Invalidate(user, backend.Kind);
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
                cache.Invalidate(user, entry.Backend);
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
        cache.Invalidate(user, entry.Backend);
    }
}

public sealed record HomeFiles(FileSystemEntry[] Favorites, FileSystemEntry[] Recent);
public enum RestoreResult { Restored, Missing, Expired, Conflict, ParentMissing }
