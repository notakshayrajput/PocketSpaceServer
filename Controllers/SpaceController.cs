using Microsoft.AspNetCore.Mvc;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/space")]
[StorageErrors]
public class SpaceController(UserStorage storage) : ControllerBase
{
    [HttpGet("drive-stats")]
    public ActionResult<DriveStats> GetStorageStats()
    {
        var root = storage.Root(User);
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        return Ok(new DriveStats
        {
            Directory = User.IsInRole("Admin") ? "Storage" : "My files",
            AvailableSpace = drive.AvailableFreeSpace,
            TotalSpace = drive.TotalSize,
            OccupiedSpace = UserStorage.FilesRecursively(root).Sum(path => new FileInfo(path).Length)
        });
    }

    [HttpGet("folder-info")]
    public ActionResult<FolderInfo> ListDirectoryContents([FromQuery] string relativePath = "")
    {
        var root = storage.Root(User);
        var path = storage.Resolve(User, relativePath);
        if (!Directory.Exists(path)) return NotFound(new { message = "Folder not found." });
        return Ok(new FolderInfo
        {
            Name = path == root ? "My files" : Path.GetFileName(path),
            LastModified = Directory.GetLastWriteTimeUtc(path),
            RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            Files = UserStorage.Entries(path).Select(entry => new FileSystemEntry
            {
                Name = Path.GetFileName(entry),
                IsFolder = Directory.Exists(entry),
                Size = Directory.Exists(entry) ? 0 : new FileInfo(entry).Length,
                LastModified = System.IO.File.GetLastWriteTimeUtc(entry),
                RelativePath = Path.GetRelativePath(root, entry).Replace('\\', '/')
            }).ToArray()
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
    public IActionResult Rename(RenameRequest request)
    {
        UserStorage.ValidateName(request.Name);
        var source = storage.Resolve(User, request.Path);
        if (source == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be renamed." });
        var destination = UserStorage.ResolveUnder(Path.GetDirectoryName(source)!, request.Name);
        if (source == destination) return NoContent();
        if (Directory.Exists(destination) || System.IO.File.Exists(destination)) return Conflict(new { message = "That name already exists." });
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else if (System.IO.File.Exists(source)) System.IO.File.Move(source, destination);
        else return NotFound(new { message = "File or folder not found." });
        return NoContent();
    }

    [HttpDelete("entry")]
    public IActionResult Delete([FromQuery] string path)
    {
        var target = storage.Resolve(User, path);
        if (target == storage.Root(User)) return BadRequest(new { message = "Your root folder cannot be deleted." });
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        else if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
        else return NotFound(new { message = "File or folder not found." });
        return NoContent();
    }
}

public sealed record CreateFolderRequest(string ParentPath, string Name);
public sealed record RenameRequest(string Path, string Name);
