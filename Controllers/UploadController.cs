using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/upload")]
[StorageErrors]
public class UploadController(UserStorage storage, FileCatalog catalog) : ControllerBase
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
        for (var index = 0; index < request.Files.Count; index++)
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
