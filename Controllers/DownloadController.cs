using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using PocketSpaceServer.Storage;
using PocketSpaceServer.Utility;

namespace PocketSpaceServer.Controllers;

[ApiController]
[Route("api/download")]
[StorageErrors]
public class DownloadController(UserStorage storage, FileCatalog catalog) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> StreamZip(DownloadRequest request)
    {
        if (request.Paths.Length == 0) return BadRequest(new { message = "No paths specified." });
        var paths = request.Paths.Select(path => storage.Resolve(User, path)).Distinct().ToArray();
        if (paths.Any(path => !System.IO.File.Exists(path) && !Directory.Exists(path)))
            return NotFound(new { message = "File or folder not found." });
        await catalog.TouchAsync(User, paths);
        if (paths.Length == 1 && System.IO.File.Exists(paths[0]))
            return PhysicalFile(paths[0], "application/octet-stream", Path.GetFileName(paths[0]));

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
                    if (System.IO.File.Exists(path)) await AddFile(archive, path, Path.GetFileName(path));
                    else
                    {
                        var folderName = path == root ? "files" : Path.GetFileName(path);
                        archive.CreateEntry(folderName + "/");
                        foreach (var file in UserStorage.FilesRecursively(path))
                            await AddFile(archive, file, folderName + "/" + Path.GetRelativePath(path, file).Replace('\\', '/'));
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

    private async Task AddFile(ZipArchive archive, string path, string name)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        await using var entry = archive.CreateEntry(name).Open();
        await source.CopyToAsync(entry, HttpContext.RequestAborted);
    }
}

public sealed class DownloadRequest
{
    public string[] Paths { get; set; } = [];
}
