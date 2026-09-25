using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Storage;

// Callers hold the account operation lock for the entire filesystem/database operation.
public sealed class FileCatalog(ApplicationDbContext db, UserStorage storage, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string Owner(ClaimsPrincipal user) => user.FindFirst("sub")!.Value;
    private static string Key(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    private string Relative(ClaimsPrincipal user, string fullPath) => Path.GetRelativePath(storage.Root(user), fullPath).Replace('\\', '/');
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private IQueryable<FileRecord> Active(ClaimsPrincipal user) => db.Files.Where(f => f.UserId == Owner(user) && f.TrashEntryId == null);

    private static IEnumerable<string> Tree(string directory)
    {
        foreach (var path in UserStorage.Entries(directory))
        {
            yield return path;
            if (Directory.Exists(path))
                foreach (var child in Tree(path)) yield return child;
        }
    }

    // Import existing disk files without moving them; browsing also discovers externally added files.
    public async Task<List<FileRecord>> IndexAsync(ClaimsPrincipal user, IEnumerable<string> paths)
    {
        var records = await Active(user).ToListAsync();
        var byPath = records.ToDictionary(f => f.PathKey);
        var result = new List<FileRecord>();
        foreach (var fullPath in paths)
        {
            var relative = Relative(user, fullPath);
            var key = Key(relative);
            if (!byPath.TryGetValue(key, out var record))
            {
                record = new FileRecord { UserId = Owner(user), RelativePath = relative, PathKey = key,
                    IsFolder = Directory.Exists(fullPath), RecentAt = File.GetLastWriteTimeUtc(fullPath) };
                db.Files.Add(record);
                byPath.Add(key, record);
            }
            result.Add(record);
        }
        await db.SaveChangesAsync();
        return result;
    }

    public FileSystemEntry Describe(ClaimsPrincipal user, FileRecord record)
    {
        var path = storage.Resolve(user, record.RelativePath);
        return new FileSystemEntry { Id = record.Id, Name = Path.GetFileName(path), RelativePath = record.RelativePath,
            IsFolder = record.IsFolder, IsFavorite = record.IsFavorite, RecentAt = record.RecentAt,
            LastModified = File.GetLastWriteTimeUtc(path), Size = record.IsFolder ? 0 : new FileInfo(path).Length };
    }

    public async Task<HomeFiles> HomeAsync(ClaimsPrincipal user)
    {
        var records = await IndexAsync(user, Tree(storage.Root(user)));
        var files = records.Where(f => !f.IsFolder).ToArray();
        return new HomeFiles(
            files.Where(f => f.IsFavorite).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).Select(f => Describe(user, f)).ToArray(),
            files.OrderByDescending(f => f.RecentAt).ThenBy(f => f.RelativePath).Take(20).Select(f => Describe(user, f)).ToArray());
    }

    public async Task<bool> FavoriteAsync(ClaimsPrincipal user, string id, bool favorite)
    {
        var file = await Active(user).SingleOrDefaultAsync(f => f.Id == id && !f.IsFolder);
        if (file is null || !File.Exists(storage.Resolve(user, file.RelativePath))) return false;
        file.IsFavorite = favorite;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task TouchAsync(ClaimsPrincipal user, IEnumerable<string> paths)
    {
        var files = paths.SelectMany(path => Directory.Exists(path) ? UserStorage.FilesRecursively(path) : new[] { path }).Distinct();
        foreach (var record in await IndexAsync(user, files)) record.RecentAt = Now;
        await db.SaveChangesAsync();
    }

    public async Task RenameAsync(ClaimsPrincipal user, string source, string destination)
    {
        await IndexAsync(user, Directory.Exists(source) ? Tree(source).Prepend(source) : new[] { source });
        var from = Relative(user, source);
        var to = Relative(user, destination);
        var fromKey = Key(from);
        var records = (await Active(user).ToListAsync()).Where(f => f.PathKey == fromKey || f.PathKey.StartsWith(fromKey + "/", StringComparison.Ordinal)).ToArray();
        var isFolder = Directory.Exists(source);
        // Clear stale metadata at the destination left by files removed outside PocketSpace.
        var destinationKey = Key(to);
        var stale = (await Active(user).ToListAsync()).Where(f => !records.Contains(f) &&
            (f.PathKey == destinationKey || f.PathKey.StartsWith(destinationKey + "/", StringComparison.Ordinal))).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Files.RemoveRange(stale);
        await db.SaveChangesAsync();
        foreach (var record in records)
        {
            record.RelativePath = to + record.RelativePath[from.Length..];
            record.PathKey = Key(record.RelativePath);
            record.RecentAt = Now;
        }
        await db.SaveChangesAsync();
        Move(source, destination, isFolder);
        try { await transaction.CommitAsync(); }
        catch { Move(destination, source, isFolder); throw; }
    }

    public async Task TrashAsync(ClaimsPrincipal user, string path)
    {
        var records = await IndexAsync(user, Directory.Exists(path) ? Tree(path).Prepend(path) : new[] { path });
        var now = Now;
        var entry = new TrashEntry { UserId = Owner(user), OriginalPath = Relative(user, path), IsFolder = Directory.Exists(path),
            Size = records.Where(f => !f.IsFolder).Sum(f => new FileInfo(storage.Resolve(user, f.RelativePath)).Length),
            TrashedAt = now, ExpiresAt = now.AddDays(7) };
        db.TrashEntries.Add(entry);
        foreach (var record in records) record.TrashEntryId = entry.Id;
        // Save intent before moving bytes, so interrupted moves are recoverable.
        await db.SaveChangesAsync();
        await FinishAsync(user, entry);
    }

    public Task<List<TrashEntry>> TrashListAsync(ClaimsPrincipal user) => db.TrashEntries
        .Where(t => t.UserId == Owner(user) && t.State == TrashState.Trashed).OrderByDescending(t => t.TrashedAt).ToListAsync();

    public Task<long> TrashSizeAsync(ClaimsPrincipal user) => db.TrashEntries.Where(t => t.UserId == Owner(user)).SumAsync(t => t.Size);

    public async Task<RestoreResult> RestoreAsync(ClaimsPrincipal user, string id)
    {
        var entry = await db.TrashEntries.SingleOrDefaultAsync(t => t.UserId == Owner(user) && t.Id == id);
        if (entry is null) return RestoreResult.Missing;
        // The deadline is enforced even if the background cleanup hasn't run yet.
        if (entry.ExpiresAt <= Now || entry.State == TrashState.Purging) return RestoreResult.Expired;
        var destination = storage.Resolve(user, entry.OriginalPath);
        if (Exists(destination)) return RestoreResult.Conflict;
        if (!Directory.Exists(Path.GetDirectoryName(destination))) return RestoreResult.ParentMissing;
        entry.State = TrashState.Restoring;
        await db.SaveChangesAsync();
        await FinishAsync(user, entry);
        return RestoreResult.Restored;
    }

    public async Task RecoverAsync(ClaimsPrincipal user)
    {
        foreach (var entry in await db.TrashEntries.Where(t => t.UserId == Owner(user) &&
            (t.State == TrashState.Moving || t.State == TrashState.Restoring)).ToListAsync())
            await FinishAsync(user, entry);
    }

    public async Task CleanupAsync(ClaimsPrincipal user, ILogger logger, CancellationToken cancellationToken)
    {
        var now = Now;
        var entries = await db.TrashEntries.Where(t => t.UserId == Owner(user) && (t.ExpiresAt <= now || t.State != TrashState.Trashed)).ToListAsync(cancellationToken);
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
        var trashPath = storage.TrashPath(user, entry.Id);
        if (entry.State == TrashState.Moving)
        {
            var source = storage.Resolve(user, entry.OriginalPath);
            if (!Exists(trashPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
                Move(source, trashPath, entry.IsFolder);
            }
            entry.State = TrashState.Trashed;
            await db.SaveChangesAsync();
        }
        else if (entry.State == TrashState.Restoring)
        {
            var destination = storage.Resolve(user, entry.OriginalPath);
            if (Exists(trashPath))
            {
                // An external writer may create a conflicting file between attempts.
                if (Exists(destination) || !Directory.Exists(Path.GetDirectoryName(destination)))
                {
                    entry.State = TrashState.Trashed;
                    await db.SaveChangesAsync();
                    throw new IOException("The original location is unavailable.");
                }
                Move(trashPath, destination, entry.IsFolder);
            }
            if (!Exists(destination)) throw new IOException("The restored file is unavailable.");
            var records = await db.Files.Where(f => f.TrashEntryId == entry.Id).ToListAsync();
            var keys = records.Select(f => f.PathKey).ToArray();
            await using var transaction = await db.Database.BeginTransactionAsync();
            // Metadata for externally removed/replaced files must not block restoration.
            await Active(user).Where(f => keys.Contains(f.PathKey)).ExecuteDeleteAsync();
            foreach (var record in records) { record.TrashEntryId = null; record.RecentAt = Now; }
            await db.SaveChangesAsync();
            db.TrashEntries.Remove(entry);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        else if (entry.State == TrashState.Purging)
        {
            // This generated, validated path is always inside this user's private trash.
            if (Directory.Exists(trashPath)) Directory.Delete(trashPath, recursive: true);
            else if (File.Exists(trashPath)) File.Delete(trashPath);
            db.TrashEntries.Remove(entry);
            await db.SaveChangesAsync();
        }
    }

    private static void Move(string source, string destination, bool isFolder)
    {
        if (isFolder) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}

public sealed record HomeFiles(FileSystemEntry[] Favorites, FileSystemEntry[] Recent);
public enum RestoreResult { Restored, Missing, Expired, Conflict, ParentMissing }
