using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSpaceServer.Data;
using PocketSpaceServer.Storage;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/upload")]
[StorageErrors]
public class UploadController(UserStorage storage, StorageManager stores, GlobalStorageLock globalLock,
    FileCatalog catalog, QuotaUsage quotaUsage,
    ApplicationDbContext db) : ControllerBase
{
    // The browser sends the File as the request body. This avoids ASP.NET's
    // multipart model-binding buffer and streams bytes directly to the backend.
    [HttpPost("stream")]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> UploadStream([FromQuery] string name, [FromQuery] string destinationPath = ".")
    {
        var length = Request.ContentLength;
        if (length is null) return StatusCode(StatusCodes.Status411LengthRequired,
            new { message = "The upload size is required." });
        UserStorage.ValidateName(name);
        var settings = await stores.SettingsAsync();
        using var globalLease = settings.GlobalLimitBytes.HasValue
            ? await globalLock.AcquireAsync(HttpContext.RequestAborted) : null;
        var backend = await stores.CurrentAsync();
        var destination = storage.Resolve(User, destinationPath);
        if ((await backend.StatAsync(destination, HttpContext.RequestAborted))?.IsFolder != true)
            return NotFound(new { message = "Folder not found." });
        var path = storage.Resolve(User, Path.Combine(destinationPath, name));
        var previous = await backend.StatAsync(path, HttpContext.RequestAborted);
        if (previous?.IsFolder == true) return Conflict(new { message = "A folder already has that name." });

        var id = User.FindFirst("sub")!.Value;
        var quota = await db.Users.Where(user => user.Id == id)
            .Select(user => user.QuotaBytes).SingleAsync(HttpContext.RequestAborted);
        var used = checked(await quotaUsage.UsedBytesAsync(User, HttpContext.RequestAborted)
            - (previous?.Size ?? 0) + length.Value);
        var globalUsed = settings.GlobalLimitBytes is null ? 0
            : await backend.TotalBytesAsync(HttpContext.RequestAborted);
        var globalAfter = checked(globalUsed - (previous?.Size ?? 0) + length.Value);
        if (used > quota || (settings.GlobalLimitBytes is long limit && globalAfter > limit))
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { message = "This upload exceeds the available storage limit. Remove files from Trash or ask an administrator for more space." });

        await backend.WriteAsync(path, Request.Body, length.Value, HttpContext.RequestAborted);
        await catalog.TouchAsync(User, new[] { path });
        return Ok(new { message = "File uploaded successfully." });
    }

    [HttpPost]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 50L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> UploadFiles([FromForm] FileUploadRequest request)
    {
        if (request.Files.Count == 0) return BadRequest(new { message = "No files uploaded." });
        var settings = await stores.SettingsAsync();
        using var globalLease = settings.GlobalLimitBytes.HasValue
            ? await globalLock.AcquireAsync(HttpContext.RequestAborted) : null;
        var backend = await stores.CurrentAsync();
        var destination = storage.Resolve(User, request.DestinationPath);
        if ((await backend.StatAsync(destination))?.IsFolder != true) return NotFound(new { message = "Folder not found." });
        var paths = request.Files.Select(file =>
        {
            UserStorage.ValidateName(file.FileName);
            return storage.Resolve(User, Path.Combine(request.DestinationPath, file.FileName));
        }).ToArray();
        var comparison = backend.Kind == "FileSystem" && OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (paths.Distinct(comparison).Count() != paths.Length)
            return BadRequest(new { message = "The upload contains duplicate file names." });
        var previous = await Task.WhenAll(paths.Select(path => backend.StatAsync(path)));
        if (previous.Any(item => item?.IsFolder == true))
            return Conflict(new { message = "A folder already has one of those names." });

        var id = User.FindFirst("sub")!.Value;
        var quota = await db.Users.Where(user => user.Id == id).Select(user => user.QuotaBytes).SingleAsync();
        var used = await quotaUsage.UsedBytesAsync(User, HttpContext.RequestAborted);
        var previousSizes = previous.Select(item => item?.Size ?? 0).ToArray();
        for (var index = 0; index < paths.Length; index++)
            used = checked(used - previousSizes[index] + request.Files[index].Length);
        var globalUsed = settings.GlobalLimitBytes is null ? 0 : await backend.TotalBytesAsync(HttpContext.RequestAborted);
        var globalAfter = globalUsed;
        for (var index = 0; index < paths.Length; index++)
            globalAfter = checked(globalAfter - previousSizes[index] + request.Files[index].Length);
        if (used > quota || (settings.GlobalLimitBytes is long limit && globalAfter > limit))
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { message = "This upload exceeds the available storage limit. Remove files from Trash or ask an administrator for more space." });

        // Apply shrinking replacements first so an accepted batch stays within quota throughout the upload.
        foreach (var index in Enumerable.Range(0, paths.Length)
            .OrderBy(index => request.Files[index].Length - previousSizes[index]))
        {
            await using (var stream = request.Files[index].OpenReadStream())
                await backend.WriteAsync(paths[index], stream, request.Files[index].Length, HttpContext.RequestAborted);
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
