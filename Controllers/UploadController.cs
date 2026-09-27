using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/upload")]
[StorageErrors]
public class UploadController(UserStorage storage, FileCatalog catalog, QuotaUsage quotaUsage,
    ApplicationDbContext db) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> UploadFiles([FromForm] FileUploadRequest request)
    {
        if (request.Files.Count == 0) return BadRequest(new { message = "No files uploaded." });
        var destination = storage.Resolve(User, request.DestinationPath);
        if (!Directory.Exists(destination)) return NotFound(new { message = "Folder not found." });
        var paths = request.Files.Select(file =>
        {
            UserStorage.ValidateName(file.FileName);
            return storage.Resolve(User, Path.Combine(request.DestinationPath, file.FileName));
        }).ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparison).Count() != paths.Length)
            return BadRequest(new { message = "The upload contains duplicate file names." });
        if (paths.Any(Directory.Exists))
            return Conflict(new { message = "A folder already has one of those names." });

        var id = User.FindFirst("sub")!.Value;
        var quota = await db.Users.Where(user => user.Id == id).Select(user => user.QuotaBytes).SingleAsync();
        var used = await quotaUsage.UsedBytesAsync(User);
        var previousSizes = paths.Select(path => System.IO.File.Exists(path) ? new FileInfo(path).Length : 0).ToArray();
        for (var index = 0; index < paths.Length; index++)
            used = checked(used - previousSizes[index] + request.Files[index].Length);
        if (used > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { message = "This upload exceeds your storage quota. Ask an administrator for more space or remove files from Trash." });

        // Apply shrinking replacements first so an accepted batch stays within quota throughout the upload.
        foreach (var index in Enumerable.Range(0, paths.Length)
            .OrderBy(index => request.Files[index].Length - previousSizes[index]))
        {
            await using (var stream = new FileStream(paths[index], FileMode.Create, FileAccess.Write, FileShare.None))
                await request.Files[index].CopyToAsync(stream, HttpContext.RequestAborted);
            await catalog.TouchAsync(User, new[] { paths[index] });
        }
        return Ok(new { message = "Files uploaded successfully." });
    }
}

public sealed class FileUploadRequest
{
    [FromForm]
    public List<IFormFile> Files { get; set; } = [];
    [FromForm]
    public string DestinationPath { get; set; } = ".";
}
