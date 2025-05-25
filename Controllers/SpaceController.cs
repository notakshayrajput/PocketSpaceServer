using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;
using PocketSpaceServer.Utility;
using System;
using System.IO;

namespace PocketSpaceServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SpaceController : ControllerBase
    {
        private readonly DirectorySettings _dirSettings;

        public SpaceController(IOptions<DirectorySettings> dirSettings)
        {
            _dirSettings = dirSettings.Value;
        }

        [HttpGet("drive-stats")]
        public ActionResult<DriveStats> GetStorageStats()
        {
            try
            {
                var dirPath = _dirSettings.TargetDirectory;

                if (!Directory.Exists(dirPath))
                {
                    return NotFound($"Directory '{dirPath}' does not exist.");
                }

                var drive = new DriveInfo(Path.GetPathRoot(dirPath)!);
                long availableSpaceBytes = drive.AvailableFreeSpace;
                long totalSpaceBytes = drive.TotalSize;
                long directorySize = Helper.GetDirectorySize(new System.IO.DirectoryInfo(dirPath));


                return Ok(new DriveStats
                {
                    Directory = dirPath,
                    AvailableSpace = availableSpaceBytes,
                    TotalSpace = totalSpaceBytes,
                    OccupiedSpace = directorySize
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }
        [HttpGet("folder-info")]
        public ActionResult<FolderInfo> ListDirectoryContents([FromQuery] string relativePath = "")
        {
            try
            {
                relativePath = Uri.UnescapeDataString(relativePath);
                // Combine base directory with relative path
                var baseDir = Path.GetFullPath(_dirSettings.TargetDirectory);
                var combinedPath = Path.GetFullPath(Path.Combine(baseDir, relativePath ?? ""));

                // Prevent path traversal outside base directory
                if (!combinedPath.StartsWith(baseDir))
                {
                    return BadRequest("Invalid path: Outside of base directory");
                }

                if (!Directory.Exists(combinedPath))
                {
                    return NotFound($"Directory '{relativePath}' does not exist.");
                }

                var filesAndFolders = new List<FileSystemEntry>();

                // Directories (depth 1)
                foreach (var dir in Directory.GetDirectories(combinedPath))
                {
                    var info = new DirectoryInfo(dir);
                    filesAndFolders.Add(new FileSystemEntry
                    {
                        Name = info.Name,
                        IsFolder = true,
                        Size = 0,
                        LastModified = info.LastWriteTimeUtc,
                        RelativePath = Path.GetRelativePath(baseDir, info.FullName).Replace("\\", "/")
                    });
                }

                // Files (depth 1)
                foreach (var file in Directory.GetFiles(combinedPath))
                {
                    var info = new FileInfo(file);
                    filesAndFolders.Add(new FileSystemEntry
                    {
                        Name = info.Name,
                        IsFolder = false,
                        Size = info.Length,
                        LastModified = info.LastWriteTimeUtc,
                        RelativePath = Path.GetRelativePath(baseDir, info.FullName).Replace("\\", "/")
                    });
                }

                var currentDirInfo = new DirectoryInfo(combinedPath);

                var folderInfo = new FolderInfo
                {
                    Name = currentDirInfo.Name,
                    LastModified = currentDirInfo.LastWriteTimeUtc,
                    RelativePath = Path.GetRelativePath(baseDir, combinedPath).Replace("\\", "/"),
                    Files = filesAndFolders.ToArray()
                };

                return Ok(folderInfo);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error: {ex.Message}");
            }
        }

    }
}
