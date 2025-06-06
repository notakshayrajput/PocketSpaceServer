using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PocketSpaceServer.Models;

namespace PocketSpaceServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UploadController : ControllerBase
    {
        private readonly DirectorySettings _dirSettings;

        public UploadController(IOptions<DirectorySettings> dirSettings)
        {
            _dirSettings = dirSettings.Value;
        }

        [HttpPost]
        [RequestSizeLimit(50L * 1024 * 1024 * 1024)] // 50GB upload size limit
        public async Task<IActionResult> UploadFiles([FromForm] FileUploadRequest request)
        {
            if (request.Files == null || request.Files.Count == 0)
                return BadRequest("No files uploaded.");

            if (string.IsNullOrWhiteSpace(request.DestinationPath))
                return BadRequest("Destination path not specified.");

            var uploadRoot = Path.GetFullPath(_dirSettings.TargetDirectory);
            var destFullPath = Path.GetFullPath(Path.Combine(uploadRoot, request.DestinationPath));

            // Prevent path traversal
            if (!destFullPath.StartsWith(uploadRoot))
                return BadRequest("Invalid destination path.");

            Directory.CreateDirectory(destFullPath);

            foreach (var file in request.Files)
            {
                var filePath = Path.Combine(destFullPath, Path.GetFileName(file.FileName));
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }
            }

            return Ok(new { Message = "Files uploaded successfully." });
        }
    }

    public class FileUploadRequest
    {
        [FromForm]
        public List<IFormFile> Files { get; set; }

        [FromForm]
        public string DestinationPath { get; set; }
    }
}
