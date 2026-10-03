using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using PocketSpaceServer.Storage;
using PocketSpaceServer.Utility;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/download")]
[StorageErrors]
public class DownloadController(UserStorage storage, StorageManager stores, FileCatalog catalog) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> StreamZip(DownloadRequest request)
    {
        if (request.Paths.Length == 0) return BadRequest(new { message = "No paths specified." });
        var backend = await stores.CurrentAsync();
        var paths = request.Paths.Select(path => storage.Resolve(User, path)).Distinct().ToArray();
        var items = await Task.WhenAll(paths.Select(path => backend.StatAsync(path)));
        if (items.Any(item => item is null))
            return NotFound(new { message = "File or folder not found." });
        await catalog.TouchAsync(User, paths);
        if (paths.Length == 1 && items[0]?.IsFolder == false)
            return File(await backend.ReadAsync(paths[0], HttpContext.RequestAborted), "application/octet-stream", items[0]!.Name);

        var root = storage.Root(User);
        // ZipArchive writes its directory synchronously on disposal. Use an automatically
        // deleted temporary file so compression works on servers that forbid synchronous response I/O.
        var output = new FileStream(Path.Combine(Path.GetTempPath(), $"pocketspace-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var path in paths)
                {
                    HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    var item = items[Array.IndexOf(paths, path)]!;
                    if (!item.IsFolder) await AddFile(backend, archive, path, item.Name);
                    else
                    {
                        var folderName = path == root ? "files" : item.Name;
                        archive.CreateEntry(folderName + "/");
                        foreach (var file in (await backend.TreeAsync(path, HttpContext.RequestAborted)).Where(child => !child.IsFolder))
                            await AddFile(backend, archive, file.Path, folderName + "/" + Path.GetRelativePath(path, file.Path).Replace('\\', '/'));
                    }
                }
            }
            output.Position = 0;
            return File(output, "application/zip", "download.zip");
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    private async Task AddFile(IStorageBackend backend, ZipArchive archive, string path, string name)
    {
        await using var source = await backend.ReadAsync(path, HttpContext.RequestAborted);
        await using var entry = archive.CreateEntry(name).Open();
        await source.CopyToAsync(entry, HttpContext.RequestAborted);
    }
}

public sealed class DownloadRequest
{
    public string[] Paths { get; set; } = [];
}
