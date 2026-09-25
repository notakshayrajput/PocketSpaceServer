using Microsoft.AspNetCore.Mvc;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/space")]
[StorageErrors]
public class SpaceController(UserStorage storage, FileCatalog catalog) : ControllerBase
{
    [HttpGet("drive-stats")]
    public async Task<ActionResult<DriveStats>> GetStorageStats()
    {
        var root = storage.Root(User);
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        return Ok(new DriveStats
        {
            Directory = User.IsInRole("Admin") ? "Storage" : "My files",
            AvailableSpace = drive.AvailableFreeSpace,
            TotalSpace = drive.TotalSize,
            OccupiedSpace = UserStorage.FilesRecursively(root).Sum(path => new FileInfo(path).Length) + await catalog.TrashSizeAsync(User)
        });
    }

    [HttpGet("folder-info")]
    public async Task<ActionResult<FolderInfo>> ListDirectoryContents([FromQuery] string relativePath = "")
    {
        var root = storage.Root(User);
        var path = storage.Resolve(User, relativePath);
        if (!Directory.Exists(path)) return NotFound(new { message = "Folder not found." });
        return Ok(new FolderInfo
        {
            Name = path == root ? "My files" : Path.GetFileName(path),
            LastModified = Directory.GetLastWriteTimeUtc(path),
            RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            Files = (await catalog.IndexAsync(User, UserStorage.Entries(path))).Select(entry => catalog.Describe(User, entry)).ToArray()
        });
    }

    [HttpPost("folders")]
    public IActionResult CreateFolder(CreateFolderRequest request)
    {
        UserStorage.ValidateName(request.Name);
        var parent = storage.Resolve(User, request.ParentPath);
        if (!Directory.Exists(parent)) return NotFound(new { message = "Parent folder not found." });
        var path = storage.Resolve(User, Path.Combine(request.ParentPath, request.Name));
        if (Directory.Exists(path) || System.IO.File.Exists(path)) return Conflict(new { message = "That name already exists." });
        Directory.CreateDirectory(path);
        return NoContent();
    }

    [HttpPost("rename")]
    public async Task<IActionResult> Rename(RenameRequest request)
    {
        UserStorage.ValidateName(request.Name);
        var source = storage.Resolve(User, request.Path);
        if (source == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be renamed." });
        var destination = UserStorage.ResolveUnder(Path.GetDirectoryName(source)!, request.Name);
        if (source == destination) return NoContent();
        if (Directory.Exists(destination) || System.IO.File.Exists(destination)) return Conflict(new { message = "That name already exists." });
        if (!Directory.Exists(source) && !System.IO.File.Exists(source)) return NotFound(new { message = "File or folder not found." });
        await catalog.RenameAsync(User, source, destination);
        return NoContent();
    }

    [HttpDelete("entry")]
    public async Task<IActionResult> Delete([FromQuery] string path)
    {
        var target = storage.Resolve(User, path);
        if (target == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be deleted." });
        if (!Directory.Exists(target) && !System.IO.File.Exists(target)) return NotFound(new { message = "File or folder not found." });
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
}

public sealed record CreateFolderRequest(string ParentPath, string Name);
public sealed record RenameRequest(string Path, string Name);
public sealed record FavoriteRequest(bool IsFavorite);
