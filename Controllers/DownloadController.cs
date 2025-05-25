using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;
using PocketSpaceServer.Utility;
using System.IO.Compression;

namespace PocketSpaceServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DownloadController : ControllerBase
    {
        private readonly DirectorySettings _dirSettings;

        public DownloadController(IOptions<DirectorySettings> dirSettings)
        {
            _dirSettings = dirSettings.Value;
        }

        [HttpPost]
        [RequestSizeLimit(2L * 1024L * 1024 * 1024L)]
        public async Task<IActionResult> StreamZip([FromBody] DownloadRequest request)
        {
            if (request.Paths == null || request.Paths.Length == 0)
                return BadRequest("No paths specified.");

            var fullPaths = new List<string>();
            foreach (var relativePath in request.Paths)
            {
                var combinedPath = Path.GetFullPath(Path.Combine(_dirSettings.TargetDirectory, relativePath));

                if (!combinedPath.StartsWith(Path.GetFullPath(_dirSettings.TargetDirectory)))
                    return BadRequest("Invalid path access.");

                if (!System.IO.File.Exists(combinedPath) && !Directory.Exists(combinedPath))
                    return NotFound($"Path not found: {relativePath}");

                fullPaths.Add(combinedPath);
            }

            // If there is only one file or folder, serve it directly
            if (fullPaths.Count == 1)
            {
                var singlePath = fullPaths.First();
                var fileInfo = new FileInfo(singlePath);

                if (System.IO.File.Exists(singlePath))
                {
                    // Directly serve the file if it's a single file request
                    var fileName = fileInfo.Name;
                    return PhysicalFile(singlePath, "application/octet-stream", fileName);
                }
                else if (Directory.Exists(singlePath))
                {
                    // If the path is a folder, zip it and serve it
                    var folderName = new DirectoryInfo(singlePath).Name;
                    return ZipFolder(singlePath, folderName); // Zip the folder and serve
                }
            }

            // If there are multiple files or directories, zip them
            return new FileCallbackResult("application/zip", async (outputStream, _) =>
            {
                using (var zipArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var path in fullPaths)
                    {
                        if (System.IO.File.Exists(path))
                        {
                            zipArchive.CreateEntryFromFile(path, Path.GetFileName(path));
                        }
                        else if (Directory.Exists(path))
                        {
                            AddDirectoryToZipStreaming(zipArchive, path, Path.GetFileName(path));
                        }
                    }
                }
            })
            {
                FileDownloadName = "download.zip"
            };
        }

        private IActionResult ZipFolder(string folderPath, string folderName)
        {
            var memoryStream = new MemoryStream();

            using (var zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddDirectoryToZipStreaming(zipArchive, folderPath, folderName);
            }

            memoryStream.Position = 0;
            return File(memoryStream, "application/zip", $"{folderName}.zip");
        }

        private void AddDirectoryToZipStreaming(ZipArchive archive, string sourceDir, string entryRoot)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                var entryPath = Path.Combine(entryRoot, relativePath).Replace("\\", "/");
                archive.CreateEntryFromFile(file, entryPath);
            }
        }
    }

    public class DownloadRequest
    {
        public string[] Paths { get; set; }
    }
}
