using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PocketSpaceServer.Data;
using PocketSpaceServer.Models;
using PocketSpaceServer.Storage;
using System.Text.Json.Serialization;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/admin/storage-settings")]
[Authorize(Roles = "Admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StorageSettingsController(ApplicationDbContext db, StorageManager stores,
    StorageOptions options, GlobalStorageLock globalLock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var settings = await stores.SettingsAsync();
        return Ok(new StorageSettingsResponse(options.Provider, settings.GlobalLimitBytes,
            options.Provider == "FileSystem" ? options.FileSystemPath : null,
            options.Provider == "S3" ? options.S3Bucket : null,
            options.Provider == "S3" ? options.S3Region : null));
    }

    [HttpPut]
    public async Task<IActionResult> Put(ChangeStorageSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request.GlobalLimitBytes is <= 0)
            return BadRequest(new { message = "The global storage limit must be positive." });

        using var lease = await globalLock.AcquireAsync(cancellationToken);
        var settings = await stores.SettingsAsync();
        settings.GlobalLimitBytes = request.GlobalLimitBytes;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}

public sealed record StorageSettingsResponse(string Backend, long? GlobalLimitBytes, string? FileSystemPath,
    string? Bucket, string? Region);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeStorageSettingsRequest(long? GlobalLimitBytes);
