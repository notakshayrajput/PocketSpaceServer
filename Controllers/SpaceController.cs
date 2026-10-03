using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/space")]
[StorageErrors]
public class SpaceController(UserStorage storage, StorageManager stores, FileCatalog catalog, QuotaUsage quotaUsage,
    ApplicationDbContext db) : ControllerBase
{
    [HttpGet("drive-stats")]
    public async Task<ActionResult<DriveStats>> GetStorageStats()
    {
        var root = storage.Root(User);
        var backend = await stores.CurrentAsync();
        var capacity = await backend.CapacityAsync();
        var settings = await stores.SettingsAsync();
        var used = await quotaUsage.UsedBytesAsync(User);
        var isAdmin = User.IsInRole("Admin");
        var globalUsed = isAdmin ? await backend.TotalBytesAsync() : 0;
        var id = User.FindFirst("sub")!.Value;
        var quotaBytes = await db.Users.Where(u => u.Id == id).Select(u => u.QuotaBytes).SingleAsync();
        return Ok(new DriveStats
        {
            Directory = User.IsInRole("Admin") ? "Storage" : "My files",
            Backend = backend.Kind,
            GlobalUsedBytes = globalUsed,
            GlobalLimitBytes = isAdmin ? settings.GlobalLimitBytes : null,
            AvailableSpace = capacity?.Available ?? 0,
            TotalSpace = capacity?.Total ?? 0,
            OccupiedSpace = used,
            QuotaBytes = quotaBytes
        });
    }

    [HttpGet("folder-info")]
    public async Task<ActionResult<FolderInfo>> ListDirectoryContents(
        [FromQuery] string relativePath = "", [FromQuery] string search = "",
        [FromQuery] string sortBy = "createdAt", [FromQuery] string direction = "desc",
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 50 || (search?.Length ?? 0) > 200 ||
            sortBy is not ("name" or "size" or "lastModified" or "createdAt") ||
            direction is not ("asc" or "desc"))
            return BadRequest(new { message = "Invalid folder listing options." });
        var root = storage.Root(User);
        var backend = await stores.CurrentAsync();
        var path = storage.Resolve(User, relativePath);
        var folder = await backend.StatAsync(path);
        if (folder?.IsFolder != true) return NotFound(new { message = "Folder not found." });
        var entries = await backend.ListAsync(path);
        var matching = entries
            .Where(entry => FolderListing.MatchesName(entry.Name, search))
            .Select(entry => new FolderListing.Entry(entry.Path, entry.Name, entry.IsFolder, entry.Size,
                entry.LastModified, entry.CreatedAt)).ToArray();
        var pagePaths = FolderListing.Sort(matching, sortBy, direction)
            .Skip(offset).Take(limit).Select(entry => entry.Path).ToArray();
        var byPath = entries.ToDictionary(entry => entry.Path);
        var pageItems = pagePaths.Select(pagePath => byPath[pagePath]).ToArray();
        var records = await catalog.IndexAsync(User, pageItems);
        // The listing already contains these attributes; avoid one more S3 HEAD per row.
        var files = records.Select((record, index) => FileCatalog.Describe(record, pageItems[index])).ToArray();
        return Ok(new FolderInfo
        {
            Name = path == root ? "My files" : Path.GetFileName(path),
            LastModified = folder.LastModified,
            RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            Files = files,
            TotalCount = matching.Length,
            NextOffset = offset + files.Length,
            HasMore = offset + files.Length < matching.Length
        });
    }

    [HttpPost("folders")]
    public async Task<IActionResult> CreateFolder(CreateFolderRequest request)
    {
        UserStorage.ValidateName(request.Name);
        var backend = await stores.CurrentAsync();
        var parent = storage.Resolve(User, request.ParentPath);
        if ((await backend.StatAsync(parent))?.IsFolder != true) return NotFound(new { message = "Parent folder not found." });
        var path = storage.Resolve(User, Path.Combine(request.ParentPath, request.Name));
        if (await backend.StatAsync(path) is not null) return Conflict(new { message = "That name already exists." });
        await backend.CreateDirectoryAsync(path);
        return NoContent();
    }

    [HttpPost("rename")]
    public async Task<IActionResult> Rename(RenameRequest request)
    {
        UserStorage.ValidateName(request.Name);
        var backend = await stores.CurrentAsync();
        var source = storage.Resolve(User, request.Path);
        if (source == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be renamed." });
        var destination = UserStorage.ResolveUnder(Path.GetDirectoryName(source)!, request.Name);
        if (source == destination) return NoContent();
        if (await backend.StatAsync(destination) is not null) return Conflict(new { message = "That name already exists." });
        if (await backend.StatAsync(source) is null) return NotFound(new { message = "File or folder not found." });
        await catalog.RenameAsync(User, source, destination);
        return NoContent();
    }

    [HttpDelete("entry")]
    public async Task<IActionResult> Delete([FromQuery] string path)
    {
        var backend = await stores.CurrentAsync();
        var target = storage.Resolve(User, path);
        if (target == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be deleted." });
        if (await backend.StatAsync(target) is null) return NotFound(new { message = "File or folder not found." });
        await catalog.TrashAsync(User, target);
        return NoContent();
    }

    [HttpGet("home")]
    public async Task<ActionResult<HomeFiles>> Home() => Ok(await catalog.HomeAsync(User));

    [HttpPut("files/{id}/favorite")]
    public async Task<IActionResult> Favorite(string id, FavoriteRequest request) =>
        await catalog.FavoriteAsync(User, id, request.IsFavorite) ? NoContent() : NotFound(new { message = "File not found." });

    [HttpGet("trash")]
    public async Task<IActionResult> Trash() => Ok((await catalog.TrashListAsync(User)).Select(entry => new
    {
        entry.Id, Name = Path.GetFileName(entry.OriginalPath), entry.OriginalPath, entry.IsFolder,
        entry.Size, entry.TrashedAt, entry.ExpiresAt
    }));

    [HttpPost("trash/{id}/restore")]
    public async Task<IActionResult> Restore(string id) => (await catalog.RestoreAsync(User, id)) switch
    {
        RestoreResult.Restored => NoContent(),
        RestoreResult.Missing => NotFound(new { message = "Trash item not found." }),
        RestoreResult.Expired => StatusCode(StatusCodes.Status410Gone, new { message = "The seven-day restore period has ended." }),
        RestoreResult.ParentMissing => Conflict(new { message = "The original folder is missing. Restore or recreate the parent folder first." }),
        _ => Conflict(new { message = "An item already exists at the original location. Rename or move it to Trash before restoring this item." })
    };

    [HttpDelete("trash/{id}")]
    public async Task<IActionResult> Purge(string id) =>
        await catalog.PurgeAsync(User, id) ? NoContent() : NotFound(new { message = "Trash item not found." });
}

public sealed record CreateFolderRequest(string ParentPath, string Name);
public sealed record RenameRequest(string Path, string Name);
public sealed record FavoriteRequest(bool IsFavorite);
